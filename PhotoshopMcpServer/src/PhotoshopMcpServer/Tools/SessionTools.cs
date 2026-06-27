using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PhotoshopMcpServer.Infrastructure;
using PhotoshopMcpServer.Services;

namespace PhotoshopMcpServer.Tools;

/// <summary>
/// Session-level tools: Photoshop status, document info, selection info.
/// </summary>
[McpServerToolType]
public sealed class SessionTools
{
    private readonly PhotoshopBridge _ps;
    private readonly ILogger<SessionTools> _logger;

    public SessionTools(PhotoshopBridge ps, ILogger<SessionTools> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    [McpServerTool(Name = "photoshop_get_session_info")]
    [Description("Get information about the current Photoshop session.")]
    public async Task<object> GetSessionInfo()
    {
        try
        {
            var script = JsHelpers.JsonPolyfill + @"
(function() {
    try {
        var info = {};
        info.version = app.version;
        try { info.build = app.build; } catch(e) { info.build = ''; }
        info.hasActiveDocument = app.documents.length > 0;
        info.documentCount = app.documents.length;

        // Active document info
        if (info.hasActiveDocument) {
            var doc = app.activeDocument;
            info.activeDocument = {
                name: doc.name,
                width: doc.width.value,
                height: doc.height.value,
                resolution: doc.resolution,
                mode: doc.mode.toString(),
                bitsPerChannel: doc.bitsPerChannel,
                layerCount: doc.artLayers.length,
            };
        }

        // All documents
        info.documents = [];
        for (var i = 0; i < app.documents.length; i++) {
            var d = app.documents[i];
            info.documents.push({
                name: d.name,
                width: d.width.value,
                height: d.height.value,
                isActive: d.name === (info.activeDocument ? info.activeDocument.name : ''),
            });
        }

        // Preferences
        info.preferences = {};
        try {
            info.preferences.rulerUnits = app.preferences.rulerUnits.toString();
            info.preferences.typeUnits = app.preferences.typeUnits.toString();
        } catch(e) {}

        // Serialize as JSON (available in PS CC+)
        try {
            return 'OK|' + _json(info);
        } catch(e) {
            // Manual pipe serialization for older PS
            var p = ['OK'];
            p.push('version=' + info.version);
            p.push('hasActiveDocument=' + info.hasActiveDocument);
            p.push('documentCount=' + info.documentCount);
            if (info.activeDocument) {
                p.push('docName=' + info.activeDocument.name);
                p.push('docWidth=' + info.activeDocument.width);
                p.push('docHeight=' + info.activeDocument.height);
            }
            return p.join('|');
        }
    } catch(e) {
        return 'ERR|' + e.toString();
    }
})();
";

            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, is_running = true, error = raw[4..] };

            // Try JSON first
            if (raw.StartsWith("OK|"))
            {
                var jsonPart = raw[3..];
                try
                {
                    var info = JsonSerializer.Deserialize<JsonElement>(jsonPart);
                    return new { success = true, is_running = true, data = info };
                }
                catch
                {
                    // Fallback: parse pipe format
                    var props = ParsePipeResult(raw);
                    return new
                    {
                        success = true,
                        is_running = true,
                        version = props.GetValueOrDefault("version", ""),
                        has_active_document = props.GetValueOrDefault("hasActiveDocument", "false") == "true",
                        active_document = props.ContainsKey("docName") ? new
                        {
                            name = props.GetValueOrDefault("docName", ""),
                            width = props.GetValueOrDefault("docWidth", ""),
                            height = props.GetValueOrDefault("docHeight", ""),
                        } : null,
                    };
                }
            }

            return new { success = false, error = $"Unexpected result: {raw}" };
        }
        catch (Exception ex)
        {
            return new { success = false, is_running = false, error = ex.Message };
        }
    }

    [McpServerTool(Name = "photoshop_get_active_document_info")]
    [Description("Get detailed information about the active document via Action Manager.")]
    public async Task<object> GetActiveDocumentInfo()
    {
        var script = JsHelpers.JsonPolyfill + @"
(function() {
    try {
        var doc = app.activeDocument;
        if (!doc) return 'ERR|No active document';

        var ref = new ActionReference();
        ref.putEnumerated(stringIDToTypeID('document'), stringIDToTypeID('ordinal'), stringIDToTypeID('targetEnum'));
        var desc = executeActionGet(ref);

        var info = {};
        try { if (desc.hasKey(stringIDToTypeID('title'))) info.name = desc.getString(stringIDToTypeID('title')); } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('width'))) info.width = desc.getUnitDoubleValue(stringIDToTypeID('width')); } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('height'))) info.height = desc.getUnitDoubleValue(stringIDToTypeID('height')); } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('resolution'))) info.resolution = desc.getUnitDoubleValue(stringIDToTypeID('resolution')); } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('mode'))) { var m = desc.getEnumerationValue(stringIDToTypeID('mode')); info.mode = m; } } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('depth'))) info.bitDepth = desc.getInteger(stringIDToTypeID('depth')); } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('numberOfLayers'))) info.layerCount = desc.getInteger(stringIDToTypeID('numberOfLayers')); } catch(e) {}
        try { if (desc.hasKey(stringIDToTypeID('fileReference'))) { var f = desc.getPath(stringIDToTypeID('fileReference')); info.path = f.toString(); } } catch(e) {}

        return 'OK|' + _json(info);
    } catch(e) {
        return 'ERR|' + e.toString();
    }
})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
            {
                if (raw.Contains("No active document"))
                    return new { success = true, error = "No active document", no_document = true };
                return new { success = false, error = raw[4..] };
            }

            if (raw.StartsWith("OK|"))
            {
                var json = raw[3..];
                try
                {
                    var info = JsonSerializer.Deserialize<JsonElement>(json);
                    return new { success = true, data = info };
                }
                catch
                {
                    return new { success = true, raw = json };
                }
            }

            return new { success = false, error = $"Unexpected result: {raw}" };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    [McpServerTool(Name = "photoshop_get_selection_info")]
    [Description("Get information about the current selection.")]
    public async Task<object> GetSelectionInfo()
    {
        var script = @"
(function() {
    try {
        var doc = app.activeDocument;
        if (!doc) return 'ERR|No active document';

        // Check if there's a selection by trying to get bounds
        try {
            var ref = new ActionReference();
            ref.putProperty(stringIDToTypeID('property'), stringIDToTypeID('selection'));
            ref.putEnumerated(stringIDToTypeID('document'), stringIDToTypeID('ordinal'), stringIDToTypeID('targetEnum'));
            var desc = executeActionGet(ref);

            if (desc.hasKey(stringIDToTypeID('selection'))) {
                var selDesc = desc.getObjectValue(stringIDToTypeID('selection'));
                if (selDesc.hasKey(stringIDToTypeID('bounds'))) {
                    var bounds = selDesc.getObjectValue(stringIDToTypeID('bounds'));
                    var left = bounds.getUnitDoubleValue(stringIDToTypeID('left'));
                    var top = bounds.getUnitDoubleValue(stringIDToTypeID('top'));
                    var right = bounds.getUnitDoubleValue(stringIDToTypeID('right'));
                    var bottom = bounds.getUnitDoubleValue(stringIDToTypeID('bottom'));
                    var w = right - left, h = bottom - top;
                    return 'OK|1|' + left + '|' + top + '|' + right + '|' + bottom + '|' + w + '|' + h + '|' + (w*h);
                }
                return 'OK|1|0|0|0|0|0|0|0';  // Has selection but no bounds
            }
            return 'OK|0';  // No selection
        } catch(e) {
            return 'OK|0';  // No selection (error means no selection)
        }
    } catch(e) {
        return 'ERR|' + e.toString();
    }
})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            var parts = raw.Split('|');
            if (parts.Length >= 2 && parts[0] == "OK")
            {
                var hasSelection = parts[1] == "1";
                if (hasSelection && parts.Length >= 9)
                {
                    return new
                    {
                        success = true,
                        has_selection = true,
                        bounds = new
                        {
                            left = double.Parse(parts[2]),
                            top = double.Parse(parts[3]),
                            right = double.Parse(parts[4]),
                            bottom = double.Parse(parts[5]),
                        },
                        width = double.Parse(parts[6]),
                        height = double.Parse(parts[7]),
                        area = double.Parse(parts[8]),
                    };
                }
                else
                {
                    return new { success = true, has_selection = false };
                }
            }

            return new { success = false, error = $"Unexpected result: {raw}" };
        }
        catch (Exception ex)
        {
            return new { success = false, has_selection = false, error = ex.Message };
        }
    }

    private static Dictionary<string, string> ParsePipeResult(string raw)
    {
        var result = new Dictionary<string, string>();
        foreach (var segment in raw.Split('|').Skip(1))
        {
            var eqIdx = segment.IndexOf('=');
            if (eqIdx > 0)
                result[segment[..eqIdx]] = segment[(eqIdx + 1)..];
        }
        return result;
    }
}
