using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PhotoshopMcpServer.Infrastructure;
using PhotoshopMcpServer.Services;

namespace PhotoshopMcpServer.Resources;

/// <summary>
/// MCP resources for reading Photoshop document state.
/// To add new resources, add methods here or create a new class with
/// [McpServerResourceType] — auto-discovered by WithResourcesFromAssembly().
/// </summary>
[McpServerResourceType]
public sealed class DocumentResources
{
    private readonly PhotoshopBridge _ps;
    private readonly ILogger<DocumentResources> _logger;

    public DocumentResources(PhotoshopBridge ps, ILogger<DocumentResources> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    [McpServerResource(Name = "photoshop://info")]
    [Description("Get Photoshop application info: version and active document status.")]
    public async Task<object> GetPhotoshopInfo()
    {
        try
        {
            var version = await _ps.GetVersionAsync();
            var docName = await _ps.GetActiveDocumentNameAsync();

            return new
            {
                version,
                has_active_document = docName != null,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [McpServerResource(Name = "photoshop://document/info")]
    [Description("Get active document info: name, dimensions, resolution, layer count.")]
    public async Task<object> GetDocumentInfo()
    {
        var script = @"
(function() {
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';
    return 'OK|' + doc.name + '|' + doc.width.value + '|' + doc.height.value + '|' + doc.resolution + '|' + doc.artLayers.length;
})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { error = raw[4..] };

            var parts = raw.Split('|');
            if (parts.Length >= 5 && parts[0] == "OK")
            {
                return new
                {
                    name = parts[1],
                    width = double.Parse(parts[2]),
                    height = double.Parse(parts[3]),
                    resolution = double.Parse(parts[4]),
                    layers_count = int.Parse(parts[5]),
                };
            }

            return new { error = "Failed to parse document info" };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    [McpServerResource(Name = "photoshop://document/layers")]
    [Description("Get all layers in the active document as a hierarchical tree.")]
    public async Task<object> GetLayers()
    {
        var script = JsHelpers.JsonPolyfill + @"
(function() {
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';

    function collectLayer(layer, idx) {
        var info = {
            index: idx,
            name: layer.name,
            visible: layer.visible,
            type: 'layer',
        };
        try { info.kind = layer.kind.toString(); } catch(e) { info.kind = 'Unknown'; }
        try {
            var b = layer.bounds;
            info.bounds = { left: b[0].value, top: b[1].value, right: b[2].value, bottom: b[3].value };
            info.width = b[2].value - b[0].value;
            info.height = b[3].value - b[1].value;
        } catch(e) { info.bounds = null; info.width = 0; info.height = 0; }
        try { info.opacity = layer.opacity; } catch(e) { info.opacity = 100; }
        try { info.blendMode = layer.blendMode.toString(); } catch(e) { info.blendMode = ''; }

        // Text layer extras
        try {
            if (layer.kind === LayerKind.TEXT) {
                var ti = layer.textItem;
                info.text = ti.contents;
                try { info.fontName = ti.font; } catch(e) {}
                try { info.fontSize = ti.size; } catch(e) {}
                try {
                    var c = ti.color;
                    info.textColor = { red: c.rgb.red, green: c.rgb.green, blue: c.rgb.blue };
                } catch(e) { info.textColor = null; }
                try { info.alignment = ti.justification.toString(); } catch(e) {}
            }
        } catch(e) {}

        // Lock
        try { info.allLocked = layer.allLocked; } catch(e) { info.allLocked = false; }
        try { info.locked = layer.locked; } catch(e) { info.locked = false; }

        return info;
    }

    function collectAll(container, startIdx) {
        var result = [];
        var idx = startIdx || 0;

        for (var i = 0; i < container.artLayers.length; i++) {
            result.push(collectLayer(container.artLayers[i], idx));
            idx++;
        }

        for (var j = 0; j < container.layerSets.length; j++) {
            var ls = container.layerSets[j];
            var group = {
                index: idx,
                name: ls.name,
                visible: ls.visible,
                type: 'group',
                kind: 'LayerSet',
            };
            try { group.opacity = ls.opacity; } catch(e) { group.opacity = 100; }
            try { group.blendMode = ls.blendMode.toString(); } catch(e) { group.blendMode = ''; }
            try {
                var b = ls.bounds;
                group.bounds = { left: b[0].value, top: b[1].value, right: b[2].value, bottom: b[3].value };
                group.width = b[2].value - b[0].value;
                group.height = b[3].value - b[1].value;
            } catch(e) { group.bounds = null; group.width = 0; group.height = 0; }

            idx++;
            var children = collectAll(ls, idx);
            group.children = children.layers;
            group.childrenCount = children.layers.length;
            result.push(group);
            idx = children.nextIdx;
        }

        return { layers: result, nextIdx: idx };
    }

    var collected = collectAll(doc, 0);
    return 'OK|' + _json({ layers: collected.layers, total_count: collected.layers.length });
})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { error = raw[4..] };

            if (raw.StartsWith("OK|"))
            {
                var json = raw[3..];
                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonElement>(json);
                    return new { layers = parsed.GetProperty("layers"), total_count = parsed.GetProperty("total_count").GetInt32() };
                }
                catch
                {
                    return new { error = "Failed to parse layer data" };
                }
            }

            return new { error = $"Unexpected result: {raw}" };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
