using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PhotoshopMcpServer.Services;

namespace PhotoshopMcpServer.Tools;

/// <summary>
/// Document-level tools: create, open, save Photoshop documents.
/// </summary>
[McpServerToolType]
public sealed class DocumentTools
{
    private readonly PhotoshopBridge _ps;
    private readonly ILogger<DocumentTools> _logger;

    public DocumentTools(PhotoshopBridge ps, ILogger<DocumentTools> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    [McpServerTool(Name = "photoshop_create_document")]
    [Description("Create a new Photoshop document.")]
    public async Task<object> CreateDocument(
        [Description("Document width in pixels")] int width = 1000,
        [Description("Document height in pixels")] int height = 1000,
        [Description("Document name")] string name = "Untitled",
        [Description("Color mode: rgb, cmyk, grayscale, bitmap, lab")] string mode = "rgb")
    {
        var validModes = new[] { "rgb", "cmyk", "grayscale", "gray", "bitmap", "lab" };
        if (!validModes.Contains(mode.ToLowerInvariant()))
        {
            return new { success = false, error = $"Invalid mode: {mode}. Valid: {string.Join(", ", validModes)}" };
        }

        var modeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rgb"] = "RGB",
            ["cmyk"] = "CMYK",
            ["grayscale"] = "GRAYSCALE",
            ["gray"] = "GRAYSCALE",
            ["bitmap"] = "BITMAP",
            ["lab"] = "LAB",
        };
        var enumName = modeMap.GetValueOrDefault(mode, "RGB");

        var script = $@"
(function() {{
    try {{
        var d = app.displayDialogs;
        app.displayDialogs = DialogModes.NO;
        var doc = app.documents.add({width}, {height}, 72, '{EscapeJs(name)}', NewDocumentMode.{enumName});
        app.displayDialogs = d;
        return 'OK|'+doc.name+'|'+doc.width.value+'|'+doc.height.value;
    }} catch(e) {{
        return 'ERR|'+e.toString();
    }}
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            var parts = raw.Split('|');
            if (parts.Length >= 4 && parts[0] == "OK")
            {
                return new
                {
                    success = true,
                    document_name = parts[1],
                    width = int.Parse(parts[2]),
                    height = int.Parse(parts[3]),
                };
            }

            return new { success = false, error = $"Unexpected JS result: {raw}" };
        }
        catch (TimeoutException)
        {
            return new { success = false, error = "Operation timed out. Photoshop may be busy or showing a dialog." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating document");
            return new { success = false, error = ex.Message, detailed_error = ex.ToString() };
        }
    }

    [McpServerTool(Name = "photoshop_open_document")]
    [Description("Open an existing Photoshop document.")]
    public async Task<object> OpenDocument(
        [Description("Absolute path to the document file")] string file_path)
    {
        if (!File.Exists(file_path))
            return new { success = false, error = $"File not found: {file_path}" };

        var script = $@"
(function() {{
    try {{
        var d = app.displayDialogs;
        app.displayDialogs = DialogModes.NO;
        var doc = app.open(File('{EscapeJs(file_path)}'));
        app.displayDialogs = d;
        return 'OK|'+doc.name+'|'+doc.width.value+'|'+doc.height.value;
    }} catch(e) {{
        return 'ERR|'+e.toString();
    }}
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            var parts = raw.Split('|');
            if (parts.Length >= 4 && parts[0] == "OK")
            {
                return new
                {
                    success = true,
                    document_name = parts[1],
                    width = int.Parse(parts[2]),
                    height = int.Parse(parts[3]),
                };
            }

            return new { success = false, error = $"Unexpected JS result: {raw}" };
        }
        catch (TimeoutException)
        {
            return new { success = false, error = "Operation timed out." };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    [McpServerTool(Name = "photoshop_save_document")]
    [Description("Save the active document.")]
    public async Task<object> SaveDocument(
        [Description("Absolute path for the saved file")] string file_path,
        [Description("File format: psd, jpg, png")] string format = "psd")
    {
        // Map format to JS save logic
        var fmt = format.ToLowerInvariant();
        var saveJs = fmt switch
        {
            "jpg" or "jpeg" => "var opts = new JPEGSaveOptions(); opts.quality = 10; doc.saveAs(f, opts, true);",
            "png" => "var opts = new PNGSaveOptions(); doc.saveAs(f, opts, true);",
            _ => "doc.saveAs(f);"  // PSD: no options needed
        };

        var script = $@"
(function() {{
    try {{
        var doc = app.activeDocument;
        if (!doc) return 'ERR|No active document';
        var f = File('{EscapeJs(file_path)}');
        var d = app.displayDialogs;
        app.displayDialogs = DialogModes.NO;
        {saveJs}
        app.displayDialogs = d;
        return 'OK';
    }} catch(e) {{
        return 'ERR|'+e.toString();
    }}
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            return new { success = true, file_path };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    private static string EscapeJs(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
    }
}
