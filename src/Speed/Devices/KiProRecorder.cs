using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Speed.Core;

namespace Speed.Devices;

/// <summary>
/// An AJA Ki Pro, controlled over its HTTP parameter API
/// (config?action=get|set&amp;paramid=...). Record and Stop values are read from the
/// unit's own descriptor list rather than assumed, because they vary by model.
/// </summary>
public sealed partial class KiProRecorder : Recorder
{
    private const string TransportStateParam = "eParamID_TransportState";
    private const string TransportCommandParam = "eParamID_TransportCommand";
    private const string MediaAvailableParam = "eParamID_CurrentMediaAvailable";
    private static readonly string[] TimecodeParams =
        ["eParamID_DisplayTimecode", "eParamID_CurrentTimecode", "eParamID_Timecode"];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _timecodeParam;
    private string? _recordValue;
    private string? _stopValue;
    private string? _codecParam;
    private string? _containerParam;
    private string? _videoFormatParam;
    private volatile bool _online;

    public KiProRecorder(RecorderConfig config) : base(config) { }

    public override string ModelName => "Ki Pro";
    public override bool ReportsMedia => true;

    public override string FormatUnavailableReason =>
        "Format Ki Pro media from its web page or front panel.";

    public override void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public override async Task ShutdownAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _loop = null;
    }

    public override Task<bool> RecordAsync() => SendCommandAsync(_recordValue, "Record");
    public override Task<bool> StopAsync() => SendCommandAsync(_stopValue, "Stop");

    private async Task<bool> SendCommandAsync(string? value, string name)
    {
        if (!_online)
        {
            LogWarn($"{name} wasn't sent because the Ki Pro isn't connected.");
            return false;
        }
        if (value == null)
        {
            LogWarn($"{name} isn't available: the Ki Pro didn't list a {name} command.");
            return false;
        }
        try
        {
            var body = await GetAsync($"config?action=set&paramid={TransportCommandParam}&value={value}", CancellationToken.None);
            return body != null;
        }
        catch (Exception ex)
        {
            LogWarn($"{name} failed: {ex.Message}");
            return false;
        }
    }

    private string BaseUrl
    {
        get
        {
            var a = Address.Trim();
            if (!a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) a = "http://" + a;
            return a.TrimEnd('/') + "/";
        }
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(BaseUrl + path, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<(string? Value, string? Name)> GetParamAsync(string param, CancellationToken ct)
    {
        var body = await GetAsync($"config?action=get&paramid={param}", ct);
        if (body == null) return (null, null);
        var value = ValueRegex().Match(body);
        var name = ValueNameRegex().Match(body);
        return (value.Success ? value.Groups[1].Value : null, name.Success ? name.Groups[1].Value : null);
    }

    [GeneratedRegex("\"value\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex ValueRegex();

    [GeneratedRegex("\"value_name\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex ValueNameRegex();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Post(() => Link = LinkState.Connecting);
                var (_, stateName) = await GetParamAsync(TransportStateParam, ct);
                if (stateName == null) throw new IOException("No reply from the Ki Pro's web interface.");

                _online = true;
                Post(() => Link = LinkState.Online);
                LogInfo($"Connected at {Address}.");
                await DiscoverAsync(ct);

                var failures = 0;
                var tick = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var (_, name) = await GetParamAsync(TransportStateParam, ct);
                        string? tcText = null;
                        if (_timecodeParam != null)
                        {
                            var tc = await GetParamAsync(_timecodeParam, ct);
                            tcText = tc.Name ?? tc.Value;
                        }
                        string? media = null;
                        if (tick++ % 12 == 0)
                            media = (await GetParamAsync(MediaAvailableParam, ct)).Value;
                        Apply(name, tcText, media);
                        if (tick % 20 == 1) await PollFormatAsync(ct);
                        failures = 0;
                        Post(() => Link = LinkState.Online);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        if (++failures >= 3) throw;
                        Post(() => Link = LinkState.NoReply);
                    }
                    await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_online) LogWarn($"Connection problem: {ex.Message}");
            }

            _online = false;
            Post(() => Link = LinkState.Offline);
            try { await Task.Delay(3000, ct); } catch { break; }
        }
        _online = false;
        Post(() => Link = LinkState.Offline);
    }

    private void Apply(string? stateName, string? tcText, string? mediaValue)
    {
        TransportState? state = null;
        if (stateName != null)
        {
            var s = stateName.ToLowerInvariant();
            state = s.Contains("record") ? TransportState.Recording
                : s.Contains("play") || s.Contains("forward") || s.Contains("reverse") ? TransportState.Playing
                : s.Contains("idle") || s.Contains("stop") || s.Contains("pause") ? TransportState.Stopped
                : TransportState.Other;
        }
        Timecode? tc = Core.Timecode.TryParse(tcText, out var parsed) ? parsed : null;
        double? pct = mediaValue != null && double.TryParse(mediaValue, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;

        Post(() =>
        {
            if (state is { } st)
            {
                Transport = st;
                if (st == TransportState.Recording) FailedToStart = false;
            }
            if (tc is { } t) Timecode = t;
            if (pct is { } v) MediaPercentFree = v;
        });
    }

    /// <summary>Finds which timecode parameter this model uses and its Record/Stop command values.</summary>
    private async Task DiscoverAsync(CancellationToken ct)
    {
        foreach (var param in TimecodeParams)
        {
            var (value, name) = await GetParamAsync(param, ct);
            if (Core.Timecode.TryParse(name ?? value, out _))
            {
                _timecodeParam = param;
                break;
            }
        }
        if (_timecodeParam == null) LogWarn("Couldn't find a timecode parameter, so timecode won't show.");

        try
        {
            var body = await GetAsync("descriptors", ct);
            if (body != null && JsonNode.Parse(body) is { } root)
            {
                // Codec, file type and video format parameter names vary by model, so find them by name.
                var ids = ParamIds(root).Distinct().ToList();
                _codecParam = ids.FirstOrDefault(i => i.Contains("Codec", StringComparison.OrdinalIgnoreCase)
                                                      && !i.Contains("Audio", StringComparison.OrdinalIgnoreCase));
                _containerParam = ids.FirstOrDefault(i => i.Contains("FileFormat", StringComparison.OrdinalIgnoreCase)
                                                          || i.Contains("FileType", StringComparison.OrdinalIgnoreCase)
                                                          || i.Contains("Container", StringComparison.OrdinalIgnoreCase));
                _videoFormatParam = ids.FirstOrDefault(i => i.Equals("eParamID_VideoFormat", StringComparison.OrdinalIgnoreCase))
                                    ?? ids.FirstOrDefault(i => i.Contains("VideoFormat", StringComparison.OrdinalIgnoreCase));
                if (_codecParam == null) LogWarn("Couldn't find a codec parameter, so the recording format won't show.");

                var command = FindParam(root, TransportCommandParam);
                if (command != null)
                {
                    foreach (var (text, value) in EnumValues(command))
                    {
                        var t = text.Trim().ToLowerInvariant();
                        if (_recordValue == null && (t == "record" || t.StartsWith("record"))) _recordValue = value;
                        if (_stopValue == null && (t == "stop" || t.StartsWith("stop"))) _stopValue = value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogWarn($"Couldn't read the Ki Pro's command list: {ex.Message}");
        }

        if (_recordValue == null || _stopValue == null)
            LogWarn("The Ki Pro didn't list Record and Stop commands, so JSPEED can't start or stop it. Status and timecode still show.");
    }

    private async Task PollFormatAsync(CancellationToken ct)
    {
        async Task<string?> Read(string? param)
        {
            if (param == null) return null;
            var (value, name) = await GetParamAsync(param, ct);
            var text = name ?? value;
            return string.IsNullOrWhiteSpace(text) ? null : FormatNames.Tidy(text);
        }
        var codec = await Read(_codecParam);
        var container = await Read(_containerParam);
        var video = await Read(_videoFormatParam);
        container = container?.ToLowerInvariant() switch
        {
            null => null,
            var c when c.Contains("quicktime") || c.Contains("mov") => ".mov",
            var c when c.Contains("mxf") => ".mxf",
            var c when c.Contains("mp4") => ".mp4",
            _ => container,
        };
        Post(() =>
        {
            Codec = codec;
            Container = container;
            VideoFormat = video;
        });
    }

    /// <summary>Every parameter id mentioned anywhere in the descriptor JSON.</summary>
    private static IEnumerable<string> ParamIds(JsonNode node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith("eParamID_", StringComparison.Ordinal):
                yield return s;
                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                    if (child != null)
                        foreach (var id in ParamIds(child)) yield return id;
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    if (child != null)
                        foreach (var id in ParamIds(child)) yield return id;
                break;
        }
    }

    /// <summary>Searches the descriptor JSON for the object describing a parameter.</summary>
    private static JsonNode? FindParam(JsonNode node, string param)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, v) in obj)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && s == param)
                        return obj;
                foreach (var (_, v) in obj)
                    if (v != null && FindParam(v, param) is { } found) return found;
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (item != null && FindParam(item, param) is { } found) return found;
                break;
        }
        return null;
    }

    /// <summary>Pairs of display text and value from any enum list inside a parameter descriptor.</summary>
    private static IEnumerable<(string Text, string Value)> EnumValues(JsonNode node)
    {
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject o)
                {
                    var text = Str(o, "text") ?? Str(o, "value_name") ?? Str(o, "name") ?? Str(o, "short_text");
                    var value = Str(o, "value") ?? Str(o, "int_value");
                    if (text != null && value != null) yield return (text, value);
                }
                if (item != null)
                    foreach (var pair in EnumValues(item)) yield return pair;
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var (_, v) in obj)
                if (v != null)
                    foreach (var pair in EnumValues(v)) yield return pair;
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
            ? (jv.TryGetValue<string>(out var s) ? s : jv.ToJsonString())
            : null;
}
