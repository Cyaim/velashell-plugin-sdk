using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace VelaShell.PluginSdk.Rpc;

/// <summary>
/// 命名管道(或任意双工流)上的轻量双向 RPC:请求/应答/通知,并发不排队,
/// 单读循环 + 写锁串行化写入。两端对称 —— 谁都能发请求与通知。
/// 断开时全部未决请求以 <see cref="RpcDisconnectedException" /> 失败并触发 <see cref="Disconnected" />。
/// <para>
/// 帧是原子的:一帧要么整帧写完、要么一个字节都不写。调用方的取消令牌只在排队拿写闸时生效,
/// 写入开始后取消只让调用方不再等待、帧照样写完;写到一半失败则整条连接作废(见 <c>WriteAsync</c>)。
/// </para>
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    /// <summary>单帧上限:fs/readAll 的 16MB 内容经 base64 后约 22MB,64MB 足够并挡住异常帧。</summary>
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcMessage>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Func<string, JsonElement?, CancellationToken, Task<object?>>? _requestHandler;
    private Action<string, JsonElement?>? _notificationHandler;
    private readonly Dictionary<string, Action<JsonElement?>> _orderedNotificationHandlers = new(StringComparer.Ordinal);
    private Task? _readLoop;
    private int _disposed;
    private int _disconnectRaised;

    /// <summary>有一帧写到一半失败过:连接已经失步,之后一个字节都不能再写(见 <see cref="WriteAsync" />)。</summary>
    private int _writeBroken;

    /// <summary>包装一条已连接的双工流(通常为 NamedPipe)。调用 <see cref="Start" /> 后才开始收发。</summary>
    public RpcConnection(Stream stream) => _stream = stream;

    /// <summary>连接断开(对端关闭/进程退出/协议错误)时触发一次。</summary>
    public event Action? Disconnected;

    /// <summary>设置请求处理器:返回值序列化为应答;抛出折叠为错误码应答。必须在 <see cref="Start" /> 前设置。</summary>
    public void SetRequestHandler(Func<string, JsonElement?, CancellationToken, Task<object?>> handler)
        => _requestHandler = handler;

    /// <summary>设置通知处理器(在线程池上调用,异常吞掉不断连)。必须在 <see cref="Start" /> 前设置。</summary>
    public void SetNotificationHandler(Action<string, JsonElement?> handler)
        => _notificationHandler = handler;

    /// <summary>
    /// 为排序敏感的执行通知注册同步接收器(必须在 Start 前设置)。
    /// 接收器在单读循环中调用,不得阻塞或等待同一连接的请求;
    /// 返回后才读取后续帧,因此执行终态应答不会越过已收到的输出。
    /// 其它通知继续使用原有并发派发,不改变整个 RPC 的调度策略。
    /// </summary>
    public void SetOrderedNotificationHandler(string method, Action<JsonElement?> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        if (_readLoop is not null)
        {
            throw new InvalidOperationException("Ordered handlers must be registered before Start.");
        }
        _orderedNotificationHandlers.Add(method, handler);
    }

    /// <summary>启动读循环。</summary>
    public void Start() => _readLoop = Task.Run(ReadLoopAsync);

    /// <summary>发出请求并等待应答;应答载荷反序列化为 <typeparamref name="TResult" />。</summary>
    /// <remarks>
    /// <paramref name="cancellationToken" /> 在请求帧开始写出之前取消,请求根本不发;开始写出之后取消,
    /// 帧照样完整写出(对端仍会执行),调用方只是立即不再等待应答。需要「取消即通知对端停止」的操作
    /// 要像执行操作那样另发取消通知,不能指望撤回已经发出的请求。
    /// </remarks>
    /// <exception cref="TimeoutException">超过 <paramref name="timeout" /> 未收到应答。</exception>
    /// <exception cref="RpcDisconnectedException">等待期间连接断开。</exception>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async Task<TResult?> RequestAsync<TResult>(string method, object? payload, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        long id = Random.Shared.NextInt64(); // 双端各自发号:随机 64 位避免两侧撞号,又无需协商号段
        var completion = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            try
            {
                await WriteAsync(new()
                {
                    Type = "req",
                    Id = id,
                    Method = method,
                    Payload = Serialize(payload)
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 发送即失败 = 连接已断:与等待期断开同一语义,调用方只需处理一种异常。
                throw new RpcDisconnectedException("RPC connection closed.");
            }
            RpcMessage response;
            try
            {
                response = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"RPC request '{method}' timed out after {timeout.TotalSeconds:0}s.");
            }
            if (response.ErrorCode is not null || response.Error is not null)
            {
                throw RpcErrorCodes.ToException(response.ErrorCode, response.Error ?? "Remote call failed.");
            }
            return response.Payload is { } result ? result.Deserialize<TResult>(JsonOptions) : default;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 发出无应答通知(尽力而为:断开时静默丢弃)。「尽力」只针对这一条通知 ——
    /// 写到一半失败的话整条连接照样作废(见 <see cref="WriteAsync" />),不会带着半帧继续用。
    /// </summary>
    public async Task NotifyAsync(string method, object? payload)
    {
        try
        {
            await WriteAsync(new() { Type = "evt", Method = method, Payload = Serialize(payload) }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or RpcDisconnectedException)
        {
            // 通知语义:对端不在就算了。
        }
    }

    /// <summary>
    /// 发送执行输出/状态通知。与普通尽力通知不同,写入失败必须上传给该执行的写泵,
    /// 由它取消执行并使终态失败;否则最后一条输出丢失会被伪装成成功。
    /// 仅限执行事件,不改变其它 RPC 通知的原有语义。
    /// </summary>
    public async Task NotifyExecutionAsync(string method, object payload)
    {
        if (method is not (PluginRpc.ExecOutput or PluginRpc.ExecSudoEvent))
        {
            throw new ArgumentException("Only execution output/state notifications are supported.", nameof(method));
        }
        try
        {
            await WriteAsync(new() { Type = "evt", Method = method, Payload = Serialize(payload) }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or RpcDisconnectedException)
        {
            // WriteAsync 无法知道头/正文已发出多少字节。任何失败都保守断连,
            // 绝不在可能失步的帧后继续发送成功或错误终态。
            FailPendingAndNotify();
            try { await DisposeAsync().ConfigureAwait(false); }
            catch { /* 保留原始发送错误给执行写泵。 */ }
            throw;
        }
    }

    private static JsonElement? Serialize(object? payload)
        => payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions);

    /// <summary>
    /// 写一帧。<b>一帧要么整帧写完、要么一个字节都不写</b> —— 长度前缀的帧流没有重新同步的办法,
    /// 写出半帧之后对端会把后续字节当成下一帧的长度去读,整条连接从此失步。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用方的 <paramref name="cancellationToken" /> <b>只管排队</b>:在拿到写闸之前取消,这一帧根本不发,
    /// 抛 <see cref="OperationCanceledException" />。拿到写闸之后这一帧就归连接所有,流写入不再带任何令牌
    /// (与读侧 <see cref="TryFillAsync" /> 不传令牌同一取舍);此时取消,调用方立即得到
    /// <see cref="OperationCanceledException" />、不陪着等写完,帧在后台照样写完整 ——
    /// 于是对端<b>仍会收到并执行</b>这条请求,与执行操作「请求帧不随调用方取消」是同一口径。
    /// </para>
    /// <para>
    /// 写入中途失败(管道断开、流被释放)时不知道对端收到了多少字节,连接即作废:
    /// 先置坏帧标记挡住排在后面的写入,再失败全部在途请求、触发 <see cref="Disconnected" /> 并释放连接。
    /// 唯一能打断一次已开始的帧写入的是释放连接(<see cref="DisposeAsync" /> 释放底层流)。
    /// 排队等写闸的调用同时听连接寿命:连接关了就以 <see cref="ObjectDisposedException" /> 退出,不会永远挂着。
    /// </para>
    /// </remarks>
    private async Task WriteAsync(RpcMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0 || Volatile.Read(ref _writeBroken) != 0, this);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await EnterWriteGateAsync(cancellationToken).ConfigureAwait(false);
        Task frame = WriteFrameAsync(header, body);
        if (!cancellationToken.CanBeCanceled || frame.IsCompleted)
        {
            await frame.ConfigureAwait(false);
            return;
        }
        try
        {
            await frame.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方不等了,帧照样写完;写失败由 WriteFrameAsync 自己作废连接,这里只把它的异常观察掉。
            _ = frame.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// 排队拿写闸。调用方取消照常生效(此时一个字节都还没写);连接关闭同样能把排队者放出来 ——
    /// <see cref="SemaphoreSlim.Dispose()" /> 不会唤醒已经在等的异步等待者,不听连接寿命的话它们会永远挂着。
    /// </summary>
    private async Task EnterWriteGateAsync(CancellationToken cancellationToken)
    {
        CancellationToken lifetime;
        try
        {
            lifetime = _lifetime.Token;
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(RpcConnection), "RPC connection closed.");
        }
        using CancellationTokenSource? linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime)
            : null;
        try
        {
            await _writeGate.WaitAsync(linked?.Token ?? lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 排队期间连接关了:与「发送即失败」同一语义,调用方只需处理断连。
            throw new ObjectDisposedException(nameof(RpcConnection), "RPC connection closed.");
        }
    }

    /// <summary>持有写闸写完整帧并释放写闸;调用前必须已拿到写闸。</summary>
    private async Task WriteFrameAsync(byte[] header, byte[] body)
    {
        try
        {
            // 前面有一帧写坏了:后面的帧一个字节都不能再写,否则对端可能把错位的字节读成一条「合法」消息。
            if (Volatile.Read(ref _writeBroken) != 0)
            {
                throw new ObjectDisposedException(nameof(RpcConnection), "RPC connection closed.");
            }
            try
            {
                await _stream.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
                await _stream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
                await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 坏帧标记在放开写闸之前置上,排在后面的写入因此一定看得到它。
                if (Interlocked.Exchange(ref _writeBroken, 1) == 0)
                {
                    _ = Task.Run(AbandonAfterBrokenWriteAsync);
                }
                throw;
            }
        }
        finally
        {
            try
            {
                _writeGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 连接已经释放(释放可能先于这里完成),写闸跟着没了。
            }
        }
    }

    /// <summary>
    /// 写坏一帧之后作废连接。放在写闸之外做:<see cref="Disconnected" /> 的处理器里若再发消息,
    /// 在写闸里同步触发它就是自己等自己。
    /// </summary>
    private async Task AbandonAfterBrokenWriteAsync()
    {
        FailPendingAndNotify();
        try
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 收尾尽力而为;连接已经标坏,不会再有帧写出去。
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] header = new byte[4];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                // 帧边界上的 EOF 是对端的正常关闭:安静退出,不制造异常
                //(进程停用/应用退出属于常规路径,调试输出不该被首发异常刷屏)。
                if (!await TryFillAsync(header, allowCleanEndOfStream: true).ConfigureAwait(false))
                {
                    return;
                }
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxFrameBytes)
                {
                    throw new IOException($"Invalid RPC frame length {length}.");
                }
                byte[] body = new byte[length];
                // false = 本端正在关闭(帧读到一半),此时 body 是半截的,不能拿去反序列化。
                if (!await TryFillAsync(body, allowCleanEndOfStream: false).ConfigureAwait(false))
                {
                    return;
                }
                RpcMessage? message = JsonSerializer.Deserialize<RpcMessage>(body, JsonOptions);
                if (message is null)
                {
                    continue;
                }
                Dispatch(message);
            }
        }
        catch
        {
            // 帧中 EOF / 管道断开 / 协议错误:统一走断开路径。
        }
        finally
        {
            FailPendingAndNotify();
        }
    }

    /// <summary>
    /// 读满缓冲区。帧边界上的 0 字节(对端干净关闭)在 <paramref name="allowCleanEndOfStream" />
    /// 时返回 false;本端主动关闭同样返回 false;帧中途 EOF 是协议破损,抛 <see cref="IOException" />。
    /// </summary>
    /// <remarks>
    /// 这里【刻意不把 <c>_lifetime.Token</c> 传给 ReadAsync】。传了的话,
    /// <see cref="DisposeAsync" /> 取消令牌会把待决的读撕成 OperationCanceledException ——
    /// 虽然被读循环的 catch 吞掉、退出码照样是 0,但每次停用插件/退出应用都在调试器里
    /// 刷一条首发异常,而主动关闭是这条路径上最常规的分支,不该长成异常的样子。
    /// 实测(字节模式 + PipeOptions.Asynchronous,与 PluginProcessClient 同配置):
    /// 释放本端流会让待决的 ReadAsync 返回 0 字节,与对端关闭的表现一致,全程无异常。
    /// 因此收尾改由 <see cref="DisposeAsync" /> 的 <c>_stream.DisposeAsync()</c> 负责,
    /// 令牌只留给循环条件、请求处理器与限时等待。
    /// </remarks>
    private async Task<bool> TryFillAsync(Memory<byte> buffer, bool allowCleanEndOfStream)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            // 关闭是先取消令牌、后释放流:这一句挡住"取消之后还去读已释放的流",
            // 否则会拿到一条 ObjectDisposedException —— 换个类型的首发异常,等于没治。
            if (_lifetime.IsCancellationRequested)
            {
                return false;
            }
            int read = await _stream.ReadAsync(buffer[offset..]).ConfigureAwait(false);
            if (read == 0)
            {
                // 本端正在关闭:流被释放导致的 0 字节不是协议破损,安静收摊。
                if (_lifetime.IsCancellationRequested || (allowCleanEndOfStream && offset == 0))
                {
                    return false;
                }
                throw new IOException("RPC stream ended mid-frame.");
            }
            offset += read;
        }
        return true;
    }

    private void Dispatch(RpcMessage message)
    {
        switch (message.Type)
        {
            case "res" when message.Id is { } id && _pending.TryRemove(id, out TaskCompletionSource<RpcMessage>? pending):
                pending.TrySetResult(message);
                break;
            case "req" when message is { Id: { } requestId, Method: { } method }:
                // 请求在线程池处理:慢处理器不阻塞读循环,天然并发(排序敏感的域由处理器内部串行化)。
                _ = Task.Run(() => HandleRequestAsync(requestId, method, message.Payload));
                break;
            case "evt" when message.Method is { } orderedMethod
                && _orderedNotificationHandlers.TryGetValue(orderedMethod, out Action<JsonElement?>? orderedHandler):
                try
                {
                    orderedHandler(message.Payload);
                }
                catch
                {
                    // 与普通通知一样隔离调用方异常;执行代理应自行记住并上报接收失败。
                }
                break;
            case "evt" when message.Method is { } eventMethod:
                _ = Task.Run(() =>
                {
                    try
                    {
                        _notificationHandler?.Invoke(eventMethod, message.Payload);
                    }
                    catch
                    {
                        // 通知处理器异常不影响连接。
                    }
                });
                break;
        }
    }

    private async Task HandleRequestAsync(long id, string method, JsonElement? payload)
    {
        RpcMessage response;
        try
        {
            Func<string, JsonElement?, CancellationToken, Task<object?>> handler = _requestHandler
                ?? throw new InvalidOperationException("No request handler configured.");
            object? result = await handler(method, payload, _lifetime.Token).ConfigureAwait(false);
            response = new() { Type = "res", Id = id, Payload = Serialize(result) };
        }
        catch (Exception ex)
        {
            response = new()
            {
                Type = "res",
                Id = id,
                ErrorCode = RpcErrorCodes.FromException(ex),
                Error = ex.Message
            };
        }
        try
        {
            await WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 应答写不出去 = 连接已断,读循环会收尾。
        }
    }

    private void FailPendingAndNotify()
    {
        foreach (long id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<RpcMessage>? pending))
            {
                pending.TrySetException(new RpcDisconnectedException("RPC connection closed."));
            }
        }
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            try
            {
                Disconnected?.Invoke();
            }
            catch
            {
                // 断开回调异常不再扩散。
            }
            Disconnected = null;
        }
    }

    /// <summary>关闭连接:停止读循环、断开底层流、失败全部未决请求(幂等)。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 关闭路径尽力而为。
        }
        if (_readLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 读循环收不了尾也不阻塞释放。
            }
        }
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}
