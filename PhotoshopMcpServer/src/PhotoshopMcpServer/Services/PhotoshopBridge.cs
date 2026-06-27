using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PhotoshopMcpServer.Services;

/// <summary>
/// STA-thread-isolated bridge for all Photoshop COM communication.
/// All COM calls are serialized through a single STA thread to avoid
/// blocking the MCP protocol layer and to enable timeout support.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PhotoshopBridge : IDisposable
{
    private readonly ILogger<PhotoshopBridge> _logger;
    private readonly Thread _staThread;
    private readonly BlockingCollection<StaWorkItem> _workQueue = new(256);
    private readonly CancellationTokenSource _disposeCts = new();

    private dynamic? _app;
    private volatile bool _comHealthy = true;

    private const int DefaultTimeoutMs = 15_000;

    public PhotoshopBridge(ILogger<PhotoshopBridge> logger)
    {
        _logger = logger;
        _staThread = new Thread(StaThreadProc)
        {
            Name = "PS-COM-STA",
            IsBackground = true,
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        // Wait for STA thread to initialize COM and Photoshop connection
        var initWork = new StaWorkItem<byte>(
            () =>
            {
                InitializeCom();
                return 1;
            },
            _disposeCts.Token
        );
        _workQueue.Add(initWork);
        initWork.Task.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Whether the COM connection is in a healthy state.
    /// After a timeout, this becomes false and all subsequent calls will fail fast.
    /// </summary>
    public bool IsHealthy => _comHealthy;

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Execute JavaScript in Photoshop and return the result string.
    /// All JS-based operations should go through this to benefit from
    /// timeout protection and STA thread isolation.
    /// </summary>
    public Task<string> ExecuteJavaScriptAsync(
        string script,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        EnsureHealthy();
        return QueueWorkAsync<string>(token =>
        {
            var jsApp = GetJsComApp();
            return ExecuteJavaScriptInternal(jsApp, script);
        }, timeoutMs, ct);
    }

    /// <summary>
    /// Get the active document name (or null).
    /// </summary>
    public Task<string?> GetActiveDocumentNameAsync(
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        EnsureHealthy();
        return QueueWorkAsync<string?>(_ =>
        {
            try
            {
                return (string?)_app!.ActiveDocument?.Name;
            }
            catch
            {
                return null;
            }
        }, timeoutMs, ct);
    }

    /// <summary>
    /// Get Photoshop version string.
    /// </summary>
    public Task<string> GetVersionAsync(
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        EnsureHealthy();
        return QueueWorkAsync<string>(_ =>
        {
            return (string)_app!.Version;
        }, timeoutMs, ct);
    }

    /// <summary>
    /// Execute an arbitrary COM operation on the STA thread.
    /// For operations that can't be done via JavaScript.
    /// </summary>
    public Task<T> RunComOperationAsync<T>(
        Func<dynamic, T> operation,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        EnsureHealthy();
        return QueueWorkAsync<T>(_ => operation(_app!), timeoutMs, ct);
    }

    // ------------------------------------------------------------------
    // STA thread
    // ------------------------------------------------------------------

    private void StaThreadProc()
    {
        try
        {
            foreach (var item in _workQueue.GetConsumingEnumerable(_disposeCts.Token))
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.SetCanceled();
                    continue;
                }

                try
                {
                    item.Execute();
                }
                catch (Exception ex)
                {
                    item.SetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private void InitializeCom()
    {
        try
        {
            var psType = Type.GetTypeFromProgID("Photoshop.Application")
                ?? throw new InvalidOperationException(
                    "Photoshop.Application COM class not registered. Is Photoshop installed?");
            _app = Activator.CreateInstance(psType)!;
            _logger.LogInformation("Photoshop COM bridge initialized successfully, version: {Version}",
                (string)_app.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Photoshop COM bridge");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private void EnsureHealthy()
    {
        if (!_comHealthy)
        {
            throw new InvalidOperationException(
                "COM connection is unhealthy (previous call timed out). Restart the MCP server.");
        }
    }

    private dynamic GetJsComApp()
    {
        // Create a fresh COM reference for JavaScript execution
        // (bypasses photoshop.api wrapper issues)
        var psType = Type.GetTypeFromProgID("Photoshop.Application")!;
        return Activator.CreateInstance(psType)!;
    }

    private Task<T> QueueWorkAsync<T>(
        Func<CancellationToken, T> work,
        int timeoutMs,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutCts = new CancellationTokenSource(timeoutMs);

        var item = new StaWorkItem<T>(() => work(ct), linkedCts.Token);
        _workQueue.Add(item);

        // Wire up timeout
        timeoutCts.Token.Register(() =>
        {
            if (!item.Task.IsCompleted)
            {
                _comHealthy = false;
                item.TrySetException(new TimeoutException(
                    $"COM operation timed out after {timeoutMs}ms"));
            }
        });

        return item.Task;
    }

    private static string ExecuteJavaScriptInternal(dynamic jsApp, string script)
    {
        // Ensure script ends with semicolon
        if (!script.TrimEnd().EndsWith(';'))
            script = script.TrimEnd() + ";";

        // Ensure script returns something
        if (!script.Contains("return ") && !script.Contains("JSON.stringify"))
            script += "\n'success';";

        // Attempt 1: default parameters
        try
        {
            var result = jsApp.DoJavaScript(script);
            if (result != null && result.ToString()!.Length > 0)
                return result.ToString()!;
            return "OK";
        }
        catch (Exception ex)
        {
            // -2147212704 = dialog-related error, try with dialogs suppressed
            if (ex.Message.Contains("-2147212704"))
            {
                var saferScript =
                    "try{" +
                    "var d=app.displayDialogs;" +
                    "app.displayDialogs=DialogModes.NO;" +
                    "var r=(function(){" + script + "})();" +
                    "app.displayDialogs=d;" +
                    "return r;" +
                    "}catch(e){return 'ERR|'+e.toString();}";
                try
                {
                    return jsApp.DoJavaScript(saferScript, null, 1).ToString()!;
                }
                catch { /* fall through */ }
            }

            // Attempt 2: mode 1
            try
            {
                var result = jsApp.DoJavaScript(script, null, 1);
                if (result != null && result.ToString()!.Length > 0)
                    return result.ToString()!;
                return "OK";
            }
            catch
            {
                // Attempt 3: mode 2 (interactive)
                try
                {
                    var result = jsApp.DoJavaScript(script, null, 2);
                    if (result != null && result.ToString()!.Length > 0)
                        return result.ToString()!;
                    return "OK";
                }
                catch
                {
                    // Last resort: wrapped with dialog suppression
                    if (!script.Contains("try {"))
                    {
                        var wrapped =
                            "try{" +
                            "var d=app.displayDialogs;" +
                            "app.displayDialogs=DialogModes.NO;" +
                            "var r=(function(){" + script + "})();" +
                            "app.displayDialogs=d;" +
                            "return r;" +
                            "}catch(e){return 'ERR|'+e.toString();}";
                        try
                        {
                            return jsApp.DoJavaScript(wrapped, null, 1).ToString()!;
                        }
                        catch (Exception lastEx)
                        {
                            return "ERR|" + lastEx.Message.Replace("|", " ");
                        }
                    }
                    return "ERR|" + ex.Message.Replace("|", " ");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // IDisposable
    // ------------------------------------------------------------------

    public void Dispose()
    {
        _disposeCts.Cancel();
        _workQueue.CompleteAdding();

        // Release COM objects on STA thread
        if (_app != null)
        {
            try
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(_app);
            }
            catch { /* best effort */ }
            _app = null;
        }

        _disposeCts.Dispose();
    }

    // ------------------------------------------------------------------
    // Nested types
    // ------------------------------------------------------------------

    private abstract class StaWorkItem
    {
        public CancellationToken CancellationToken { get; }
        protected StaWorkItem(CancellationToken ct) => CancellationToken = ct;
        public abstract void Execute();
        public abstract void SetCanceled();
        public abstract void SetException(Exception ex);
    }

    private sealed class StaWorkItem<T> : StaWorkItem
    {
        private readonly Func<T> _work;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StaWorkItem(Func<T> work, CancellationToken ct) : base(ct)
        {
            _work = work;
        }

        public Task<T> Task => _tcs.Task;

        public override void Execute()
        {
            var result = _work();
            _tcs.TrySetResult(result);
        }

        public override void SetCanceled()
        {
            _tcs.TrySetCanceled();
        }

        public override void SetException(Exception ex)
        {
            _tcs.TrySetException(ex);
        }

        public void TrySetException(Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }
}
