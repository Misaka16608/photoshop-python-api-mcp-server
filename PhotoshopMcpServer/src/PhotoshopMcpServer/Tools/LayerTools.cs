using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PhotoshopMcpServer.Services;

namespace PhotoshopMcpServer.Tools;

/// <summary>
/// Layer operations: create, read, update, delete, export.
/// To add new layer tools, add methods to this class (or create a new class
/// with [McpServerToolType] — it will be auto-discovered by WithToolsFromAssembly()).
/// </summary>
[McpServerToolType]
public sealed class LayerTools
{
    private readonly PhotoshopBridge _ps;
    private readonly ILogger<LayerTools> _logger;

    public LayerTools(PhotoshopBridge ps, ILogger<LayerTools> logger)
    {
        _ps = ps;
        _logger = logger;
    }

    // ==================================================================
    // create_text_layer
    // ==================================================================

    [McpServerTool(Name = "photoshop_create_text_layer")]
    [Description("Create a text layer in the active document.")]
    public async Task<object> CreateTextLayer(
        [Description("Text content")] string text,
        [Description("X position in pixels")] int x = 100,
        [Description("Y position in pixels")] int y = 100,
        [Description("Font size")] int size = 24,
        [Description("Red color component (0-255)")] int color_r = 0,
        [Description("Green color component (0-255)")] int color_g = 0,
        [Description("Blue color component (0-255)")] int color_b = 0)
    {
        var script = $@"
(function() {{
    try {{
        var doc = app.activeDocument;
        if (!doc) return 'ERR|No active document';
        var layer = doc.artLayers.add();
        layer.kind = LayerKind.TEXT;
        var ti = layer.textItem;
        ti.contents = '{EscapeJs(text)}';
        ti.position = [{x}, {y}];
        ti.size = {size};
        var c = new SolidColor();
        c.rgb.red = {color_r};
        c.rgb.green = {color_g};
        c.rgb.blue = {color_b};
        ti.color = c;
        return 'OK|'+layer.name;
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
            return new { success = true, layer_name = parts.Length > 1 ? parts[1] : "Unknown" };
        }
        catch (TimeoutException)
        {
            return new { success = false, error = "Operation timed out." };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message, detailed_error = ex.ToString() };
        }
    }

    // ==================================================================
    // create_solid_color_layer
    // ==================================================================

    [McpServerTool(Name = "photoshop_create_solid_color_layer")]
    [Description("Create a solid color fill layer.")]
    public async Task<object> CreateSolidColorLayer(
        [Description("Red component (0-255)")] int color_r = 255,
        [Description("Green component (0-255)")] int color_g = 0,
        [Description("Blue component (0-255)")] int color_b = 0,
        [Description("Layer name")] string name = "Color Fill")
    {
        var script = $@"
(function() {{
    try {{
        var doc = app.activeDocument;
        if (!doc) return 'ERR|No active document';
        var layer = doc.artLayers.add();
        layer.name = '{EscapeJs(name)}';
        var c = new SolidColor();
        c.rgb.red = {color_r};
        c.rgb.green = {color_g};
        c.rgb.blue = {color_b};
        doc.selection.selectAll();
        doc.selection.fill(c);
        doc.selection.deselect();
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

            return new { success = true, layer_name = name };
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

    // ==================================================================
    // get_layer_info (comprehensive — all in one JS call)
    // ==================================================================

    [McpServerTool(Name = "photoshop_get_layer_info")]
    [Description("Get detailed information about a layer by name or index.")]
    public async Task<object> GetLayerInfo(
        [Description("Layer name (fuzzy match)")] string layer_name = "",
        [Description("Layer index (0-based, takes priority)")] int layer_index = -1)
    {
        if (string.IsNullOrEmpty(layer_name) && layer_index < 0)
            return new { success = false, error = "Must specify layer_name or layer_index" };

        var searchField = layer_index >= 0 ? "index" : "name";
        var searchValue = layer_index >= 0 ? layer_index.ToString() : JsonSerializer.Serialize(layer_name);
        var searchValueJson = JsonSerializer.Serialize(layer_name);

        var script = $@"
(function() {{
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';

    // Flatten all layers/groups
    function flatten(container, result) {{
        for (var i = 0; i < container.artLayers.length; i++)
            result.push(container.artLayers[i]);
        for (var j = 0; j < container.layerSets.length; j++) {{
            result.push(container.layerSets[j]);
            flatten(container.layerSets[j], result);
        }}
    }}
    var all = [];
    flatten(doc, all);

    var target = null;
    if ('{searchField}' === 'index') {{
        var idx = {searchValue};
        if (idx >= 0 && idx < all.length) target = all[idx];
    }} else {{
        var q = {searchValueJson};
        var ql = q.toLowerCase();
        // Priority 1: exact name, non-LayerSet
        for (var i = 0; i < all.length; i++) {{
            if (all[i].name.toLowerCase() === ql && all[i].typename !== 'LayerSet')
                {{ target = all[i]; break; }}
        }}
        // Priority 2: exact name, any type
        if (!target)
            for (var i = 0; i < all.length; i++)
                if (all[i].name.toLowerCase() === ql) {{ target = all[i]; break; }}
        // Priority 3: substring, non-LayerSet
        if (!target)
            for (var i = 0; i < all.length; i++)
                if (all[i].name.toLowerCase().indexOf(ql) !== -1 && all[i].typename !== 'LayerSet')
                    {{ target = all[i]; break; }}
        // Priority 4: substring, any type
        if (!target)
            for (var i = 0; i < all.length; i++)
                if (all[i].name.toLowerCase().indexOf(ql) !== -1)
                    {{ target = all[i]; break; }}
    }}
    if (!target) return 'ERR|Layer not found';

    var info = {{}};
    info.name = target.name;
    try {{ info.visible = target.visible; }} catch(e) {{ info.visible = true; }}
    try {{ info.kind = target.kind; }} catch(e) {{ info.kind = -1; }}
    info.typename = target.typename;

    var b = target.bounds;
    info.bl = b[0].value; info.bt = b[1].value;
    info.br = b[2].value; info.bb = b[3].value;

    try {{ info.opacity = target.opacity; }} catch(e) {{ info.opacity = 100; }}
    try {{ info.blendMode = target.blendMode.toString(); }} catch(e) {{ info.blendMode = ''; }}
    try {{ info.allLocked = target.allLocked; }} catch(e) {{ info.allLocked = false; }}
    try {{ info.locked = target.locked; }} catch(e) {{ info.locked = false; }}

    // Text layer extras
    if (info.kind === 2) {{
        try {{
            var ti = target.textItem;
            info.text = ti.contents;
            info.fontName = ti.font;
            info.fontSize = ti.size;
            // Color via Action Manager (ti.color broken in PS 2020)
            var ref = new ActionReference();
            ref.putIdentifier(stringIDToTypeID('layer'), target.id);
            var desc = executeActionGet(ref);
            var td = desc.getObjectValue(stringIDToTypeID('textKey'));
            var sl = td.getList(stringIDToTypeID('textStyleRange'));
            if (sl.count > 0) {{
                var sr = sl.getObjectValue(0);
                var ts = sr.getObjectValue(stringIDToTypeID('textStyle'));
                var cd = null;
                if (ts.hasKey(stringIDToTypeID('color')))
                    cd = ts.getObjectValue(stringIDToTypeID('color'));
                else if (ts.hasKey(stringIDToTypeID('baseParentStyle'))) {{
                    var bp = ts.getObjectValue(stringIDToTypeID('baseParentStyle'));
                    if (bp.hasKey(stringIDToTypeID('color')))
                        cd = bp.getObjectValue(stringIDToTypeID('color'));
                }}
                if (cd && cd.hasKey(stringIDToTypeID('red'))) {{
                    info.tcR = Math.round(cd.getDouble(stringIDToTypeID('red')));
                    info.tcG = Math.round(cd.getDouble(stringIDToTypeID('green')));
                    info.tcB = Math.round(cd.getDouble(stringIDToTypeID('blue')));
                }}
            }}
        }} catch(e) {{}}
    }}

    // LayerSet extras
    if (info.typename === 'LayerSet') {{
        try {{
            info.childrenCount = target.artLayers.length + target.layerSets.length;
        }} catch(e) {{ info.childrenCount = 0; }}
    }}

    var parts = ['OK'];
    for (var k in info) parts.push(k + '=' + info[k]);
    return parts.join('|');
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            // Parse pipe-delimited key=value pairs
            var props = new Dictionary<string, string>();
            var segments = raw.Split('|');
            for (int i = 1; i < segments.Length; i++)
            {
                var eqIdx = segments[i].IndexOf('=');
                if (eqIdx > 0)
                    props[segments[i][..eqIdx]] = segments[i][(eqIdx + 1)..];
            }

            var kindStr = props.GetValueOrDefault("kind", "-1");
            var kind = int.TryParse(kindStr, out var k) ? k : -1;
            var isLayerSet = props.GetValueOrDefault("typename") == "LayerSet";

            var result = new Dictionary<string, object?>
            {
                ["success"] = true,
                ["name"] = props.GetValueOrDefault("name", ""),
                ["visible"] = props.GetValueOrDefault("visible", "true") == "true",
                ["kind"] = kind,
                ["type"] = isLayerSet ? "layerSet" : "layer",
            };

            // Bounds
            if (double.TryParse(props.GetValueOrDefault("bl"), out var left) &&
                double.TryParse(props.GetValueOrDefault("bt"), out var top) &&
                double.TryParse(props.GetValueOrDefault("br"), out var right) &&
                double.TryParse(props.GetValueOrDefault("bb"), out var bottom))
            {
                result["bounds"] = new { left, top, right, bottom };
                result["width"] = right - left;
                result["height"] = bottom - top;
            }

            result["opacity"] = double.TryParse(props.GetValueOrDefault("opacity", "100"), out var op) ? op : 100.0;
            result["blend_mode"] = props.GetValueOrDefault("blendMode", "");
            result["all_locked"] = props.GetValueOrDefault("allLocked", "false") == "true";
            result["locked"] = props.GetValueOrDefault("locked", "false") == "true";

            // Text properties
            if (kind == 2)
            {
                result["text"] = props.GetValueOrDefault("text", "");
                result["font_name"] = props.GetValueOrDefault("fontName", "");
                result["font_size"] = double.TryParse(props.GetValueOrDefault("fontSize"), out var fs) ? fs : 0;
                if (int.TryParse(props.GetValueOrDefault("tcR"), out var tr) &&
                    int.TryParse(props.GetValueOrDefault("tcG"), out var tg) &&
                    int.TryParse(props.GetValueOrDefault("tcB"), out var tb))
                {
                    result["text_color"] = new { red = tr, green = tg, blue = tb };
                }
                else
                {
                    result["text_color"] = null;
                }
            }

            // Group properties
            if (isLayerSet)
            {
                result["children_count"] = int.TryParse(props.GetValueOrDefault("childrenCount"), out var cc) ? cc : 0;
            }

            return result;
        }
        catch (TimeoutException)
        {
            return new { success = false, error = "Operation timed out." };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message, detailed_error = ex.ToString() };
        }
    }

    // ==================================================================
    // delete_layer
    // ==================================================================

    [McpServerTool(Name = "photoshop_delete_layer")]
    [Description("Delete a layer by name or index.")]
    public async Task<object> DeleteLayer(
        [Description("Layer name (fuzzy match)")] string layer_name = "",
        [Description("Layer index (0-based, takes priority)")] int layer_index = -1)
    {
        if (string.IsNullOrEmpty(layer_name) && layer_index < 0)
            return new { success = false, error = "Must specify layer_name or layer_index" };

        var searchField = layer_index >= 0 ? "index" : "name";
        var searchValue = layer_index >= 0 ? layer_index.ToString() : JsonSerializer.Serialize(layer_name);
        var searchValueJson = JsonSerializer.Serialize(layer_name);

        var script = $@"
(function() {{
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';
    function flatten(container, result) {{
        for (var i = 0; i < container.artLayers.length; i++) result.push(container.artLayers[i]);
        for (var j = 0; j < container.layerSets.length; j++) {{ result.push(container.layerSets[j]); flatten(container.layerSets[j], result); }}
    }}
    var all = []; flatten(doc, all);
    var target = null;
    if ('{searchField}' === 'index') {{
        var idx = {searchValue};
        if (idx >= 0 && idx < all.length) target = all[idx];
    }} else {{
        var q = {searchValueJson};
        var ql = q.toLowerCase();
        var match = function(t) {{ return t.name.toLowerCase().indexOf(ql) !== -1; }};
        for (var i = 0; i < all.length; i++) {{ if (all[i].name.toLowerCase() === ql) {{ target = all[i]; break; }} }}
        if (!target) for (var i = 0; i < all.length; i++) {{ if (match(all[i])) {{ target = all[i]; break; }} }}
    }}
    if (!target) return 'ERR|Layer not found';
    var name = target.name;
    target.remove();
    return 'OK|'+name;
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            var deletedName = raw.Split('|').Skip(1).FirstOrDefault() ?? "Unknown";
            return new { success = true, deleted_layer = deletedName, message = $"Layer '{deletedName}' deleted successfully" };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    // ==================================================================
    // modify_layer
    // ==================================================================

    [McpServerTool(Name = "photoshop_modify_layer")]
    [Description("Modify layer properties: rename, reposition, change text, visibility, opacity, blend mode.")]
    public async Task<object> ModifyLayer(
        [Description("Layer name to find (fuzzy match)")] string layer_name = "",
        [Description("Layer index (0-based, takes priority)")] int layer_index = -1,
        [Description("New layer name")] string new_name = "",
        [Description("New text content (text layers only)")] string text = "",
        [Description("New X position")] int? x = null,
        [Description("New Y position")] int? y = null,
        [Description("Set visibility")] bool? visible = null,
        [Description("Set opacity (0-100)")] double? opacity = null,
        [Description("Blend mode: multiply, screen, overlay, etc.")] string blend_mode = "")
    {
        if (string.IsNullOrEmpty(layer_name) && layer_index < 0)
            return new { success = false, error = "Must specify layer_name or layer_index" };

        // Pre-validate blend mode
        var bmEnum = "";
        if (!string.IsNullOrEmpty(blend_mode))
        {
            bmEnum = BlendModeMap.GetEnumName(blend_mode) ?? "";
            if (string.IsNullOrEmpty(bmEnum))
                return new { success = false, error = $"Unknown blend mode '{blend_mode}'" };
        }

        // Build all conditionals inside JS to avoid C# string-concat syntax bugs
        var searchField = layer_index >= 0 ? "index" : "name";
        var searchValue = layer_index >= 0 ? layer_index.ToString() : JsonSerializer.Serialize(layer_name);
        var searchValueJson = JsonSerializer.Serialize(layer_name);
        var escNewName = EscapeJs(new_name);
        var escText = EscapeJs(text);
        var visVal = visible.HasValue ? (visible.Value ? "true" : "false") : "";
        var xTag = x.HasValue ? "1" : "0";
        var xVal = x.HasValue ? x.Value.ToString() : "0";
        var yTag = y.HasValue ? "1" : "0";
        var yVal = y.HasValue ? y.Value.ToString() : "0";

        var script = $@"
(function() {{
    var doc = app.activeDocument;
    if (!doc) return 'ERR|No active document';
    function flatten(c, r) {{ for (var i=0;i<c.artLayers.length;i++) r.push(c.artLayers[i]); for (var j=0;j<c.layerSets.length;j++){{r.push(c.layerSets[j]);flatten(c.layerSets[j],r);}} }}
    var all = []; flatten(doc, all);
    var target = null;
    if ('{searchField}'==='index') {{ var idx={searchValue}; if(idx>=0&&idx<all.length) target=all[idx]; }}
    else {{ var q={searchValueJson},ql=q.toLowerCase();
        for(var i=0;i<all.length;i++) if(all[i].name.toLowerCase()===ql){{target=all[i];break;}}
        if(!target) for(var i=0;i<all.length;i++) if(all[i].name.toLowerCase().indexOf(ql)!==-1){{target=all[i];break;}} }}
    if(!target) return 'ERR|Layer not found';

    var changes = [];
    var newName='{escNewName}';
    var newText='{escText}';
    var hasX={xTag}, hasY={yTag}, nx={xVal}, ny={yVal};
    var hasVis={(!string.IsNullOrEmpty(visVal) ? "1" : "0")}, visVal={(!string.IsNullOrEmpty(visVal) ? visVal : "false")};
    var hasOp={((opacity.HasValue ? "1" : "0"))}, opVal={(opacity.HasValue ? opacity.Value.ToString() : "0")};
    var bmStr='{EscapeJs(bmEnum)}';

    if (newName !== '') {{
        try {{ target.name = newName; changes.push('Renamed'); }}
        catch(e) {{ return 'ERR|Rename failed: '+e.toString(); }}
    }}
    if (newText !== '') {{
        if (target.kind !== LayerKind.TEXT) return 'ERR|Cannot set text on non-text layer';
        try {{ target.textItem.contents = newText; changes.push('Text set'); }}
        catch(e) {{ return 'ERR|Text failed: '+e.toString(); }}
    }}
    if (hasX == 1 || hasY == 1) {{
        try {{
            var b = target.bounds;
            var mx = (hasX == 1) ? nx : b[0].value;
            var my = (hasY == 1) ? ny : b[1].value;
            if (target.kind === LayerKind.TEXT) target.textItem.position = [mx, my];
            else target.translate(mx - b[0].value, my - b[1].value);
            changes.push('Moved');
        }} catch(e) {{ return 'ERR|Move failed: '+e.toString(); }}
    }}
    if (hasVis == 1) {{
        try {{ target.visible = visVal; changes.push('Visibility'); }}
        catch(e) {{ return 'ERR|Visibility failed: '+e.toString(); }}
    }}
    if (hasOp == 1) {{
        try {{ target.opacity = opVal; changes.push('Opacity'); }}
        catch(e) {{ return 'ERR|Opacity failed: '+e.toString(); }}
    }}
    if (bmStr !== '') {{
        try {{ target.blendMode = BlendMode[bmStr]; changes.push('Blend mode'); }}
        catch(e) {{ return 'ERR|Blend mode failed: '+e.toString(); }}
    }}

    if (changes.length === 0) return 'OK|'+target.name+'|No changes requested|0';
    var msg = 'Applied '+changes.length+' change(s): ';
    for (var c=0; c<changes.length; c++) {{ if (c>0) msg+=', '; msg+=changes[c]; }}
    return 'OK|'+target.name+'|'+msg+'|'+changes.length;
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            var parts = raw.Split('|');
            if (parts.Length >= 4)
            {
                return new
                {
                    success = true,
                    layer_name = parts[1],
                    message = parts[2],
                    changes_count = int.Parse(parts[3]),
                };
            }

            return new { success = true, message = "No changes made" };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    // ==================================================================
    // get_layer_thumbnail
    // ==================================================================

    [McpServerTool(Name = "photoshop_get_layer_thumbnail")]
    [Description("Export a layer as a base64-encoded PNG thumbnail.")]
    public async Task<object> GetLayerThumbnail(
        [Description("Layer name (fuzzy match)")] string layer_name = "",
        [Description("Layer index (0-based, takes priority)")] int layer_index = -1,
        [Description("Maximum width/height of thumbnail")] int max_size = 256)
    {
        if (string.IsNullOrEmpty(layer_name) && layer_index < 0)
            return new { success = false, error = "Must specify layer_name or layer_index" };

        var searchField = layer_index >= 0 ? "index" : "name";
        var searchValue = layer_index >= 0 ? layer_index.ToString() : JsonSerializer.Serialize(layer_name);
        var searchValueJson = JsonSerializer.Serialize(layer_name);
        var tempPath = Path.GetTempFileName() + ".png";
        var escapedPath = tempPath.Replace("\\", "\\\\");

        var script = $@"
(function() {{
    var origDialogs = app.displayDialogs;
    app.displayDialogs = DialogModes.NO;
    var origDoc, origRulerUnits, origActiveLayer;
    try {{
        origDoc = app.activeDocument;
        if (!origDoc) return 'ERR|No active document';
        origRulerUnits = app.preferences.rulerUnits;
        origActiveLayer = origDoc.activeLayer;
        app.preferences.rulerUnits = Units.PIXELS;

        function findByIndex(container, targetIdx, counter) {{
            if (counter === undefined) counter = {{v: 0}};
            for (var i = 0; i < container.artLayers.length; i++) {{
                if (counter.v === targetIdx) return container.artLayers[i];
                counter.v++;
            }}
            for (var j = 0; j < container.layerSets.length; j++) {{
                if (counter.v === targetIdx) return container.layerSets[j];
                counter.v++;
                var found = findByIndex(container.layerSets[j], targetIdx, counter);
                if (found) return found;
            }}
            return null;
        }}

        var targetLayer;
        if ('{searchField}' === 'index') {{
            targetLayer = findByIndex(origDoc, {searchValue});
        }} else {{
            var q = {searchValueJson}, ql = q.toLowerCase();
            function flatFind(c) {{
                for (var i = 0; i < c.artLayers.length; i++) if (c.artLayers[i].name.toLowerCase().indexOf(ql) !== -1) return c.artLayers[i];
                for (var j = 0; j < c.layerSets.length; j++) {{ var f = flatFind(c.layerSets[j]); if (f) return f; }}
                return null;
            }}
            targetLayer = flatFind(origDoc);
        }}
        if (!targetLayer) return 'ERR|Layer not found';
        if (targetLayer.typename === 'LayerSet') return 'ERR|Cannot thumbnail a layer group';

        origDoc.activeLayer = targetLayer;
        var bounds = targetLayer.bounds;
        var docW = origDoc.width.value, docH = origDoc.height.value;
        var left = Math.max(0, Math.floor(bounds[0].value));
        var top = Math.max(0, Math.floor(bounds[1].value));
        var right = Math.min(docW, Math.ceil(bounds[2].value));
        var bottom = Math.min(docH, Math.ceil(bounds[3].value));
        var w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0) return 'ERR|Layer has no visible pixels';

        // Selection by Action Manager
        var sd = new ActionDescriptor();
        var sr = new ActionReference();
        sr.putProperty(stringIDToTypeID('channel'), stringIDToTypeID('selection'));
        sd.putReference(stringIDToTypeID('target'), sr);
        var rect = new ActionDescriptor();
        rect.putUnitDouble(stringIDToTypeID('top'), stringIDToTypeID('pixelsUnit'), top);
        rect.putUnitDouble(stringIDToTypeID('left'), stringIDToTypeID('pixelsUnit'), left);
        rect.putUnitDouble(stringIDToTypeID('bottom'), stringIDToTypeID('pixelsUnit'), bottom);
        rect.putUnitDouble(stringIDToTypeID('right'), stringIDToTypeID('pixelsUnit'), right);
        sd.putObject(stringIDToTypeID('to'), stringIDToTypeID('rectangle'), rect);
        executeAction(stringIDToTypeID('set'), sd, DialogModes.NO);
        origDoc.selection.copy(false);
        origDoc.selection.deselect();
        origDoc.activeLayer = origActiveLayer;

        var tempDoc = app.documents.add(w, h, origDoc.resolution, '_ps_thumb', NewDocumentMode.RGB, DocumentFill.TRANSPARENT);
        try {{
            tempDoc.paste();
            if (w > {max_size} || h > {max_size}) {{
                var pct = Math.min({max_size}/w*100, {max_size}/h*100);
                tempDoc.resizeImage(new UnitValue(pct, '%'), new UnitValue(pct, '%'), undefined, ResampleMethod.BICUBICSHARPER);
            }}
            var f = new File('{escapedPath}');
            if (f.exists) f.remove();
            var opts = new PNGSaveOptions(); opts.compression = 9;
            tempDoc.saveAs(f, opts, true);
            var rw = tempDoc.width.value, rh = tempDoc.height.value;
            tempDoc.close(SaveOptions.DONOTSAVECHANGES);
            return 'OK|'+rw+'|'+rh+'|'+w+'|'+h;
        }} catch(e) {{
            tempDoc.close(SaveOptions.DONOTSAVECHANGES);
            throw e;
        }}
    }} catch(e) {{
        return 'ERR|'+e.toString();
    }} finally {{
        try {{
            if (origRulerUnits !== undefined) app.preferences.rulerUnits = origRulerUnits;
            app.displayDialogs = origDialogs;
            if (origDoc) {{ app.activeDocument = origDoc; if (origActiveLayer) origDoc.activeLayer = origActiveLayer; origDoc.selection.deselect(); }}
        }} catch(e) {{}}
    }}
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script, timeoutMs: 30_000);
            if (raw.StartsWith("ERR|"))
            {
                TryDelete(tempPath);
                return new { success = false, error = raw[4..] };
            }

            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                TryDelete(tempPath);
                return new { success = false, error = "Thumbnail export failed — no output file generated" };
            }

            var imgData = await File.ReadAllBytesAsync(tempPath);
            TryDelete(tempPath);

            return new
            {
                success = true,
                thumbnail_base64 = Convert.ToBase64String(imgData),
                format = "png",
                size_bytes = imgData.Length,
                layer_name = layer_name,
            };
        }
        catch (TimeoutException)
        {
            TryDelete(tempPath);
            return new { success = false, error = "Operation timed out." };
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return new { success = false, error = ex.Message };
        }
    }

    // ==================================================================
    // export_layer
    // ==================================================================

    [McpServerTool(Name = "photoshop_export_layer")]
    [Description("Export a specific layer as a PNG file to disk.")]
    public async Task<object> ExportLayer(
        [Description("Absolute output path for the PNG file")] string output_path,
        [Description("Layer name (fuzzy match)")] string layer_name = "",
        [Description("Layer index (0-based, takes priority)")] int layer_index = -1,
        [Description("Export scale factor (0-1.0], 0.25 = 25%%)")] double scale = 1.0,
        [Description("Trim transparent pixels")] bool trim = false)
    {
        // Validate
        if (string.IsNullOrEmpty(output_path))
            return new { success = false, error = "output_path is required" };

        if (scale <= 0 || scale > 1.0)
            return new { success = false, error = $"scale must be > 0 and <= 1.0, got {scale}" };

        if (string.IsNullOrEmpty(layer_name) && layer_index < 0)
            return new { success = false, error = "Must specify layer_name or layer_index" };

        var fullPath = Path.GetFullPath(output_path);
        var dir = Path.GetDirectoryName(fullPath)!;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { return new { success = false, error = $"Cannot create directory {dir}: {ex.Message}" }; }

        var searchField = layer_index >= 0 ? "index" : "name";
        var searchValue = layer_index >= 0 ? layer_index.ToString() : JsonSerializer.Serialize(layer_name);
        var searchValueJson = JsonSerializer.Serialize(layer_name);
        var escapedPath = fullPath.Replace("\\", "\\\\");

        var script = $@"
(function() {{
    var origDialogs = app.displayDialogs;
    app.displayDialogs = DialogModes.NO;
    var origDoc, origRulerUnits, origActiveLayer;
    try {{
        origDoc = app.activeDocument;
        if (!origDoc) return 'ERR|No active document';
        origRulerUnits = app.preferences.rulerUnits;
        origActiveLayer = origDoc.activeLayer;
        app.preferences.rulerUnits = Units.PIXELS;

        function findByIndex(container, targetIdx, counter) {{
            if (counter === undefined) counter = {{v:0}};
            for (var i=0;i<container.artLayers.length;i++) {{ if(counter.v===targetIdx) return container.artLayers[i]; counter.v++; }}
            for (var j=0;j<container.layerSets.length;j++) {{ if(counter.v===targetIdx) return container.layerSets[j]; counter.v++; var f=findByIndex(container.layerSets[j],targetIdx,counter); if(f) return f; }}
            return null;
        }}
        var targetLayer;
        if ('{searchField}'==='index') {{ targetLayer=findByIndex(origDoc,{searchValue}); }}
        else {{ var q={searchValueJson},ql=q.toLowerCase();
            function flatFind(c) {{ for(var i=0;i<c.artLayers.length;i++) if(c.artLayers[i].name.toLowerCase().indexOf(ql)!==-1) return c.artLayers[i]; for(var j=0;j<c.layerSets.length;j++){{var f=flatFind(c.layerSets[j]);if(f)return f;}} return null; }}
            targetLayer=flatFind(origDoc); }}
        if(!targetLayer) return 'ERR|Layer not found';
        if(targetLayer.typename==='LayerSet') return 'ERR|Cannot export a layer group';

        origDoc.activeLayer = targetLayer;
        var bounds = targetLayer.bounds;
        var docW=origDoc.width.value, docH=origDoc.height.value;
        var left=Math.max(0,Math.floor(bounds[0].value)), top=Math.max(0,Math.floor(bounds[1].value));
        var right=Math.min(docW,Math.ceil(bounds[2].value)), bottom=Math.min(docH,Math.ceil(bounds[3].value));
        var w=right-left, h=bottom-top;
        if(w<=0||h<=0) return 'ERR|Layer has no renderable pixels';

        var origW=w, origH=h;

        var sd=new ActionDescriptor(), sRef=new ActionReference();
        sRef.putProperty(stringIDToTypeID('channel'),stringIDToTypeID('selection'));
        sd.putReference(stringIDToTypeID('target'),sRef);
        var rect=new ActionDescriptor();
        rect.putUnitDouble(stringIDToTypeID('top'),stringIDToTypeID('pixelsUnit'),top);
        rect.putUnitDouble(stringIDToTypeID('left'),stringIDToTypeID('pixelsUnit'),left);
        rect.putUnitDouble(stringIDToTypeID('bottom'),stringIDToTypeID('pixelsUnit'),bottom);
        rect.putUnitDouble(stringIDToTypeID('right'),stringIDToTypeID('pixelsUnit'),right);
        sd.putObject(stringIDToTypeID('to'),stringIDToTypeID('rectangle'),rect);
        executeAction(stringIDToTypeID('set'),sd,DialogModes.NO);
        origDoc.selection.copy(false);
        origDoc.selection.deselect();
        origDoc.activeLayer=origActiveLayer;

        var tempDoc=app.documents.add(w,h,origDoc.resolution,'_ps_export',NewDocumentMode.RGB,DocumentFill.TRANSPARENT);
        try{{
            tempDoc.paste();
            if({scale}>0 && {scale}<1.0){{ var sPct={scale}*100; tempDoc.resizeImage(new UnitValue(sPct,'%'),new UnitValue(sPct,'%'),undefined,ResampleMethod.BICUBICSHARPER); }}
            if({(trim ? "true" : "false")}){{ tempDoc.trim(TrimType.TRANSPARENT,true,true,true,true); }}
            var f=new File('{escapedPath}'); if(f.exists) f.remove();
            var opts=new PNGSaveOptions(); opts.compression=9;
            tempDoc.saveAs(f,opts,true);
            var rw=tempDoc.width.value, rh=tempDoc.height.value;
            tempDoc.close(SaveOptions.DONOTSAVECHANGES);
            return 'OK|'+rw+'|'+rh+'|'+origW+'|'+origH;
        }}catch(e){{ tempDoc.close(SaveOptions.DONOTSAVECHANGES); throw e; }}
    }}catch(e){{ return 'ERR|'+e.toString(); }}
    finally {{
        try{{ if(origRulerUnits!==undefined) app.preferences.rulerUnits=origRulerUnits; app.displayDialogs=origDialogs;
            if(origDoc){{ app.activeDocument=origDoc; if(origActiveLayer) origDoc.activeLayer=origActiveLayer; origDoc.selection.deselect(); }} }}catch(e){{}}
    }}
}})();
";

        try
        {
            var raw = await _ps.ExecuteJavaScriptAsync(script, timeoutMs: 60_000);
            if (raw.StartsWith("ERR|"))
                return new { success = false, error = raw[4..] };

            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                return new { success = false, error = "Export failed — no output file generated" };

            var parts = raw.Split('|');
            int expW = 0, expH = 0, origW = 0, origH = 0;
            if (parts.Length >= 5)
            {
                int.TryParse(parts[1], out expW);
                int.TryParse(parts[2], out expH);
                int.TryParse(parts[3], out origW);
                int.TryParse(parts[4], out origH);
            }

            return new
            {
                success = true,
                output_path = fullPath,
                width = expW,
                height = expH,
                original_width = origW,
                original_height = origH,
                layer_name = layer_name,
            };
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

    // ==================================================================
    // Helpers
    // ==================================================================

    private static string EscapeJs(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}

/// <summary>
/// Maps blend mode strings to Photoshop BlendMode enum names.
/// </summary>
internal static class BlendModeMap
{
    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["normal"] = "NORMAL",
        ["dissolve"] = "DISSOLVE",
        ["darken"] = "DARKEN",
        ["multiply"] = "MULTIPLY",
        ["color_burn"] = "COLORBURN",
        ["linear_burn"] = "LINEARBURN",
        ["darker_color"] = "DARKERCOLOR",
        ["lighten"] = "LIGHTEN",
        ["screen"] = "SCREEN",
        ["color_dodge"] = "COLORDODGE",
        ["linear_dodge"] = "LINEARDODGE",
        ["lighter_color"] = "LIGHTERCOLOR",
        ["overlay"] = "OVERLAY",
        ["soft_light"] = "SOFTLIGHT",
        ["hard_light"] = "HARDLIGHT",
        ["vivid_light"] = "VIVIDLIGHT",
        ["linear_light"] = "LINEARLIGHT",
        ["pin_light"] = "PINLIGHT",
        ["hard_mix"] = "HARDMIX",
        ["difference"] = "DIFFERENCE",
        ["exclusion"] = "EXCLUSION",
        ["subtract"] = "SUBTRACT",
        ["divide"] = "DIVIDE",
        ["hue"] = "HUE",
        ["saturation"] = "SATURATION",
        ["color"] = "COLOR",
        ["luminosity"] = "LUMINOSITY",
    };

    public static string? GetEnumName(string blendMode)
    {
        var normalized = blendMode.Trim().Replace(" ", "_");
        return _map.GetValueOrDefault(normalized);
    }
}
