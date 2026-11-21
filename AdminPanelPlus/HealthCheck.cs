using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using Shared.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace AdminPanelPlus;

/// <summary>
/// Проверка провайдеров с сервера Lampac: поиск -> ссылка -> манифест -> первый сегмент.
/// Запросы идут на localhost самого Lampac с заголовком lcrqpasswd (как у других модулей),
/// поэтому accsdb и group_hide не мешают, а провайдеры вызываются тем же кодом, что и у плеера.
/// </summary>
[Authorization(redirectUri: "/adminpanel/auth")]
public class HealthCheckController : BaseController
{
    const string SamplesFile = "database/adminpanel/healthcheck.json";
    const int MaxSamples = 30;
    const int MaxWalkDepth = 5;
    const int MaxBodyBytes = 262144;

    static readonly string[] Kinds = { "movie", "serial", "anime" };
    static readonly SemaphoreSlim Gate = new(4, 4);
    static readonly SemaphoreSlim FileLock = new(1, 1);

    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    #region модель тестового тайтла
    sealed class Sample
    {
        public string id { get; set; }
        public string kind { get; set; } = "movie";
        public string title { get; set; }
        public string original_title { get; set; }
        public int year { get; set; }
        public long tmdb_id { get; set; }
        public string imdb_id { get; set; }
        public long kinopoisk_id { get; set; }
        public string original_language { get; set; }
    }

    // TMDB/IMDb/КиноПоиск id заданы в настройках по умолчанию; если провайдер ищет только по названию, используются title/year.
    static List<Sample> DefaultSamples() => new()
    {
        new Sample { id = "movie-1", kind = "movie", title = "Интерстеллар", original_title = "Interstellar", year = 2014, tmdb_id = 157336, imdb_id = "tt0816692", kinopoisk_id = 258687, original_language = "en" },
        new Sample { id = "serial-1", kind = "serial", title = "Во все тяжкие", original_title = "Breaking Bad", year = 2008, tmdb_id = 1396, imdb_id = "tt0903747", kinopoisk_id = 404900, original_language = "en" },
        new Sample { id = "anime-1", kind = "anime", title = "Атака титанов", original_title = "Shingeki no Kyojin", year = 2013, tmdb_id = 1429, imdb_id = "tt2560140", kinopoisk_id = 749374, original_language = "ja" }
    };

    static string Query(Sample s, bool forProvider)
    {
        var sb = new StringBuilder();

        void add(string k, object v)
        {
            if (v == null)
                return;
            string str = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(str) || str == "0")
                return;
            if (sb.Length > 0)
                sb.Append('&');
            sb.Append(k).Append('=').Append(HttpUtility.UrlEncode(str));
        }

        if (s.tmdb_id > 0)
        {
            add("id", s.tmdb_id);
            add("tmdb_id", s.tmdb_id);
            add("source", "tmdb");
        }

        add("imdb_id", s.imdb_id);
        add("kinopoisk_id", s.kinopoisk_id);
        add("title", s.title);
        add("original_title", s.original_title);
        add("original_language", s.original_language);
        add("year", s.year);

        if (sb.Length > 0)
            sb.Append('&');
        sb.Append("serial=").Append(s.kind == "serial" ? "1" : "0");

        if (s.kind == "anime")
            sb.Append("&anime=1");

        if (forProvider)
            sb.Append("&rjson=true");
        else if (s.tmdb_id > 0 && string.IsNullOrEmpty(s.imdb_id))
            sb.Append("&external_ids=true");

        return sb.ToString();
    }
    #endregion

    #region API: тестовые тайтлы
    [HttpGet]
    [Route("/adminpanel/api/healthcheck/samples")]
    public async Task<ActionResult> GetSamples()
    {
        var (list, custom) = await LoadSamplesAsync().ConfigureAwait(false);
        return Json(new { custom, samples = list });
    }

    [HttpPost]
    [Route("/adminpanel/api/healthcheck/samples")]
    public async Task<ActionResult> SaveSamples()
    {
        string body = await ReadBodyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return Err(400, "empty body");

        List<Sample> input;
        try
        {
            var root = JToken.Parse(body);
            var arr = root is JObject o ? o["samples"] as JArray : root as JArray;
            if (arr == null)
                return Err(400, "samples must be an array");
            input = arr.ToObject<List<Sample>>();
        }
        catch (JsonException ex)
        {
            return Err(400, "invalid json", ex.Message);
        }

        // пустой список = сброс к настройкам по умолчанию
        if (input == null || input.Count == 0)
        {
            await FileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (System.IO.File.Exists(SamplesFile))
                    System.IO.File.Delete(SamplesFile);
            }
            finally
            {
                FileLock.Release();
            }

            return Json(new { ok = true, custom = false, samples = DefaultSamples() });
        }

        if (input.Count > MaxSamples)
            return Err(400, "too many samples", $"max {MaxSamples}");

        var clean = new List<Sample>();
        int n = 0;
        foreach (var s in input)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.title))
                return Err(400, "title is required");

            n++;
            s.kind = Kinds.Contains(s.kind) ? s.kind : "movie";
            s.id = string.IsNullOrWhiteSpace(s.id) ? $"{s.kind}-custom-{n}" : Clip(s.id, 40);
            s.title = Clip(s.title.Trim(), 200);
            s.original_title = Clip(s.original_title?.Trim(), 200);
            s.imdb_id = Clip(s.imdb_id?.Trim(), 20);
            s.original_language = Clip(s.original_language?.Trim(), 10);
            if (s.year < 0 || s.year > 2100) s.year = 0;
            if (s.tmdb_id < 0) s.tmdb_id = 0;
            if (s.kinopoisk_id < 0) s.kinopoisk_id = 0;
            clean.Add(s);
        }

        if (clean.Select(s => s.id).Distinct().Count() != clean.Count)
            return Err(400, "sample ids must be unique");

        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string dir = Path.GetDirectoryName(SamplesFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = SamplesFile + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonConvert.SerializeObject(clean, Formatting.Indented), new UTF8Encoding(false));
            System.IO.File.Move(tmp, SamplesFile, true);
        }
        catch (IOException ex)
        {
            return Err(500, "failed to write samples", ex.Message);
        }
        finally
        {
            FileLock.Release();
        }

        return Json(new { ok = true, custom = true, samples = clean });
    }

    static async Task<(List<Sample> list, bool custom)> LoadSamplesAsync()
    {
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (System.IO.File.Exists(SamplesFile))
            {
                var list = JsonConvert.DeserializeObject<List<Sample>>(System.IO.File.ReadAllText(SamplesFile, Encoding.UTF8));
                if (list != null && list.Count > 0)
                    return (list, true);
            }
        }
        catch (Exception)
        {
            // повреждённый файл: работаем с настройками по умолчанию, файл не трогаем
        }
        finally
        {
            FileLock.Release();
        }

        return (DefaultSamples(), false);
    }
    #endregion

    #region API: список провайдеров под тайтл и запуск проверки
    /// <summary>Какие включённые провайдеры Lampac предлагает для этого тайтла (lite/events). Адреса берутся у сервера, не угадываются по имени секции.</summary>
    [HttpGet]
    [Route("/adminpanel/api/healthcheck/providers")]
    public async Task<ActionResult> Providers(string sample)
    {
        var s = await FindSampleAsync(sample).ConfigureAwait(false);
        if (s == null)
            return Err(404, "sample not found");

        var r = await GetAsync(LocalBase() + "/lite/events?" + Query(s, false), 30, true, MaxBodyBytes * 4).ConfigureAwait(false);
        if (!r.ok)
            return Err(502, "lite/events failed", r.error);

        JToken parsed;
        try
        {
            parsed = JToken.Parse(r.text);
        }
        catch (JsonException)
        {
            return Err(502, "lite/events returned not json", Clip(r.text, 200));
        }

        if (parsed is JObject accs && accs.Value<bool?>("accsdb") == true)
            return Err(502, "lite/events denied by accsdb");

        var list = new List<object>();
        if (parsed is JArray arr)
        {
            foreach (var it in arr.OfType<JObject>())
            {
                string url = it.Value<string>("url");
                if (string.IsNullOrEmpty(url))
                    continue;
                list.Add(new { name = it.Value<string>("name"), balanser = it.Value<string>("balanser"), url });
            }
        }

        return Json(new { sample = s.id, providers = list });
    }

    public sealed class RunRequest
    {
        public string sample { get; set; }
        public string url { get; set; }
    }

    /// <summary>Одна проверка: один провайдер на одном тайтле. Клиент вызывает её по очереди, поэтому прогресс виден сразу.</summary>
    [HttpPost]
    [Route("/adminpanel/api/healthcheck/run")]
    public async Task<ActionResult> Run()
    {
        string body = await ReadBodyAsync().ConfigureAwait(false);
        RunRequest req;
        try
        {
            req = JsonConvert.DeserializeObject<RunRequest>(body ?? string.Empty);
        }
        catch (JsonException ex)
        {
            return Err(400, "invalid json", ex.Message);
        }

        if (req == null || string.IsNullOrWhiteSpace(req.url) || string.IsNullOrWhiteSpace(req.sample))
            return Err(400, "sample and url are required");

        var s = await FindSampleAsync(req.sample).ConfigureAwait(false);
        if (s == null)
            return Err(404, "sample not found");

        // проверяем только адреса самого Lampac: иначе эндпоинт стал бы открытым прокси для запросов с паролем
        if (!TryLocalizeLite(req.url, out string providerUrl))
            return Err(400, "url must point to this Lampac (/lite/...)");

        await Gate.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            var result = await CheckAsync(providerUrl, s, HttpContext.RequestAborted).ConfigureAwait(false);
            return Content(JsonConvert.SerializeObject(result), "application/json; charset=utf-8");
        }
        finally
        {
            Gate.Release();
        }
    }
    #endregion

    #region цепочка проверки
    sealed class Step
    {
        public string name { get; set; }
        public bool ok { get; set; }
        public long ms { get; set; }
        public string detail { get; set; }
    }

    sealed class CheckResult
    {
        // ok - прошло всё; nodata - у провайдера нет такого тайтла (не поломка); fail - сбой на шаге failedAt
        public string status { get; set; }
        public string failedAt { get; set; }
        public string via { get; set; }
        public List<Step> steps { get; set; } = new();
        public List<HostInfo> hosts { get; set; } = new();
    }

    /// <summary>Реальный хост, к которому ходил Lampac (за /proxy/ раскрывается из токена): его и нужно добавлять в маршрутизацию VPN.</summary>
    sealed class HostInfo
    {
        public string host { get; set; }
        public string role { get; set; }          // manifest | segment
        public bool ok { get; set; }
        public string error { get; set; }
        public bool upstreamProxy { get; set; }   // Lampac идёт к нему через свой внешний proxy из настроек провайдера
        public string[] ips { get; set; } = Array.Empty<string>();
    }

    static async Task<CheckResult> CheckAsync(string providerUrl, Sample s, CancellationToken ct)
    {
        var res = await CheckCoreAsync(providerUrl, s, ct).ConfigureAwait(false);
        await ResolveHostsAsync(res.hosts, ct).ConfigureAwait(false);
        return res;
    }

    static async Task<CheckResult> CheckCoreAsync(string providerUrl, Sample s, CancellationToken ct)
    {
        var res = new CheckResult();

        CheckResult fail(string at)
        {
            res.status = "fail";
            res.failedAt = at;
            return res;
        }

        string sep = providerUrl.Contains('?') ? "&" : "?";
        string url = providerUrl + sep + Query(s, true);

        #region 1. поиск, затем спуск по первому элементу до ссылки на видео
        string mediaUrl = null;
        string callUrl = null;
        Dictionary<string, string> mediaHeaders = null;

        for (int depth = 0; depth <= MaxWalkDepth; depth++)
        {
            string stepName = depth == 0 ? "search" : "browse";
            var sw = Stopwatch.StartNew();
            var r = await GetAsync(url, 40, true, MaxBodyBytes * 4, ct).ConfigureAwait(false);
            sw.Stop();

            if (!r.ok)
            {
                string why503 = r.error == "HTTP 503" && depth == 0 ? "HTTP 503: провайдер не дал результата (тайтла нет в каталоге или его сайт не ответил)" : r.error;
                if (!string.IsNullOrEmpty(r.body))
                    why503 += "; ответ провайдера: " + r.body;
                res.steps.Add(new Step { name = stepName, ok = false, ms = sw.ElapsedMilliseconds, detail = why503 });
                return fail(stepName);
            }

            JToken doc;
            try
            {
                doc = JToken.Parse(r.text);
            }
            catch (JsonException)
            {
                res.steps.Add(new Step { name = stepName, ok = false, ms = sw.ElapsedMilliseconds, detail = "ответ не JSON: " + Clip(r.text, 120) });
                return fail(stepName);
            }

            if (doc is JObject o && o.Value<bool?>("accsdb") == true)
            {
                res.steps.Add(new Step { name = stepName, ok = false, ms = sw.ElapsedMilliseconds, detail = "отказ: " + Clip(o.Value<string>("msg") ?? "accsdb", 120) });
                return fail(stepName);
            }

            var pick = ((doc as JObject)?["data"] as JArray)?.OfType<JObject>().FirstOrDefault(i => !string.IsNullOrEmpty(i.Value<string>("url")));
            if (pick == null)
            {
                string msg = (doc as JObject)?.Value<string>("msg") ?? (doc as JObject)?.Value<string>("error");
                res.steps.Add(new Step { name = stepName, ok = true, ms = sw.ElapsedMilliseconds, detail = msg ?? "пустой ответ" });
                if (depth == 0)
                {
                    res.status = "nodata";
                    return res;
                }

                return fail(stepName);
            }

            res.steps.Add(new Step { name = stepName, ok = true, ms = sw.ElapsedMilliseconds, detail = $"{pick.Value<string>("method") ?? "?"}: {Clip(pick.Value<string>("title") ?? pick.Value<string>("name") ?? pick.Value<string>("voice_name"), 60)}" });

            string method = pick.Value<string>("method");
            string next = pick.Value<string>("url");

            if (method == "link")
            {
                if (!TryLocalizeLite(next, out url))
                {
                    res.steps.Add(new Step { name = "browse", ok = false, detail = "адрес вне Lampac: " + Clip(next, 100) });
                    return fail("browse");
                }

                url = WithRjson(url);
                continue;
            }

            if (method == "call")
            {
                callUrl = next;
                break;
            }

            // play (или метод не указан): url и есть ссылка на видео
            mediaUrl = next;
            mediaHeaders = ReadHeaders(pick);
            break;
        }

        if (callUrl == null && mediaUrl == null)
        {
            res.steps.Add(new Step { name = "browse", ok = false, detail = $"не дошли до ссылки за {MaxWalkDepth} шагов" });
            return fail("browse");
        }
        #endregion

        #region 2. call: получить ссылку на видео, как это делает плеер
        if (callUrl != null)
        {
            if (!TryLocalizeLite(callUrl, out string localCall))
            {
                res.steps.Add(new Step { name = "link", ok = false, detail = "адрес вне Lampac: " + Clip(callUrl, 100) });
                return fail("link");
            }

            var sw = Stopwatch.StartNew();
            var r = await GetAsync(localCall, 40, true, MaxBodyBytes * 4, ct).ConfigureAwait(false);
            sw.Stop();

            string resolved = null;
            string why = string.IsNullOrEmpty(r.body) ? r.error : r.error + "; ответ провайдера: " + r.body;
            if (r.ok)
            {
                try
                {
                    var j = JToken.Parse(r.text);
                    resolved = (j as JObject)?["url"]?.ToString();
                    mediaHeaders = ReadHeaders(j as JObject);
                    if (string.IsNullOrEmpty(resolved))
                    {
                        var first = ((j as JObject)?["data"] as JArray)?.OfType<JObject>().FirstOrDefault(i => !string.IsNullOrEmpty(i.Value<string>("url")));
                        resolved = first?.Value<string>("url");
                        mediaHeaders ??= ReadHeaders(first);
                    }
                    if (string.IsNullOrEmpty(resolved))
                        why = (j as JObject)?.Value<string>("msg") ?? "в ответе нет ссылки";
                }
                catch (JsonException)
                {
                    why = "ответ не JSON: " + Clip(r.text, 120);
                }
            }

            if (string.IsNullOrEmpty(resolved))
            {
                res.steps.Add(new Step { name = "link", ok = false, ms = sw.ElapsedMilliseconds, detail = why });
                return fail("link");
            }

            res.steps.Add(new Step { name = "link", ok = true, ms = sw.ElapsedMilliseconds });
            mediaUrl = resolved;
        }
        #endregion

        #region 3. манифест и первый сегмент (или первые байты файла)
        return await CheckMediaAsync(res, mediaUrl, mediaHeaders, ct).ConfigureAwait(false);
        #endregion
    }

    static async Task<CheckResult> CheckMediaAsync(CheckResult res, string mediaUrl, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        CheckResult fail(string at)
        {
            res.status = "fail";
            res.failedAt = at;
            return res;
        }

        string abs = ToAbsolute(mediaUrl, null);
        if (abs == null)
        {
            res.steps.Add(new Step { name = "manifest", ok = false, detail = "некорректная ссылка: " + Clip(mediaUrl, 100) });
            return fail("manifest");
        }

        bool local = TryLocalize(abs, out string reqUrl);
        res.via = local ? "proxy" : "direct";
        if (!local)
            reqUrl = abs;

        var sw = Stopwatch.StartNew();
        var m = await GetAsync(reqUrl, 25, local, MaxBodyBytes, ct, headers: local ? null : headers).ConfigureAwait(false);
        sw.Stop();
        long manifestMs = sw.ElapsedMilliseconds;
        NoteHost(res, reqUrl, local, "manifest", m.ok, m.error);

        if (!m.ok)
        {
            string mdetail = m.error;
            if (!local && Uri.TryCreate(reqUrl, UriKind.Absolute, out var mu))
                mdetail += "; путь: " + Clip(mu.AbsolutePath, 90); // без query: там обычно токены
            res.steps.Add(new Step { name = "manifest", ok = false, ms = sw.ElapsedMilliseconds, detail = mdetail });
            return fail("manifest");
        }

        string text = m.text ?? string.Empty;

        // HLS
        if (text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            string playlistUrl = m.finalUrl ?? reqUrl;
            string body = text;

            // master-плейлист: берём первый вариант и грузим его
            if (body.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                string variant = FirstUri(body, afterTag: "#EXT-X-STREAM-INF");
                string variantAbs = ToAbsolute(variant, playlistUrl);
                if (variantAbs == null)
                {
                    res.steps.Add(new Step { name = "manifest", ok = false, ms = sw.ElapsedMilliseconds, detail = "master без вариантов" });
                    return fail("manifest");
                }

                bool vl = TryLocalize(variantAbs, out string vreq);
                if (!vl)
                    vreq = variantAbs;

                var sw2 = Stopwatch.StartNew();
                var v = await GetAsync(vreq, 25, vl, MaxBodyBytes, ct, headers: vl ? null : headers).ConfigureAwait(false);
                sw2.Stop();
                NoteHost(res, vreq, vl, "manifest", v.ok, v.error);

                if (!v.ok || !(v.text ?? string.Empty).TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
                {
                    res.steps.Add(new Step { name = "manifest", ok = false, ms = manifestMs + sw2.ElapsedMilliseconds, detail = "вариант недоступен: " + (v.error ?? "не плейлист") });
                    return fail("manifest");
                }

                playlistUrl = v.finalUrl ?? vreq;
                body = v.text;
                manifestMs += sw2.ElapsedMilliseconds;
            }

            res.steps.Add(new Step { name = "manifest", ok = true, ms = manifestMs, detail = "HLS" });

            string seg = FirstUri(body, afterTag: "#EXTINF");
            string segAbs = ToAbsolute(seg, playlistUrl);
            if (segAbs == null)
            {
                res.steps.Add(new Step { name = "segment", ok = false, detail = "в плейлисте нет сегментов" });
                return fail("segment");
            }

            return await CheckRangeAsync(res, segAbs, headers, ct).ConfigureAwait(false);
        }

        // DASH
        if (text.Contains("<MPD", StringComparison.Ordinal))
        {
            res.steps.Add(new Step { name = "manifest", ok = true, ms = sw.ElapsedMilliseconds, detail = "DASH" });
            res.status = "ok";
            return res;
        }

        // обычный файл: ответ получен, первые байты прочитаны
        if (m.bytes > 0)
        {
            res.steps.Add(new Step { name = "manifest", ok = true, ms = sw.ElapsedMilliseconds, detail = $"файл, {m.contentType}" });
            res.steps.Add(new Step { name = "segment", ok = true, detail = $"прочитано {m.bytes} байт" });
            res.status = "ok";
            return res;
        }

        res.steps.Add(new Step { name = "manifest", ok = false, ms = sw.ElapsedMilliseconds, detail = "пустой ответ" });
        return fail("manifest");
    }

    static async Task<CheckResult> CheckRangeAsync(CheckResult res, string segAbs, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        bool local = TryLocalize(segAbs, out string reqUrl);
        if (!local)
            reqUrl = segAbs;

        var sw = Stopwatch.StartNew();
        var g = await GetAsync(reqUrl, 25, local, 2048, ct, range: "bytes=0-2047", headers: local ? null : headers).ConfigureAwait(false);

        // часть CDN отвечает ошибкой именно на Range: повторяем без него, читаем те же первые байты
        if (!g.ok && g.error != null && g.error.StartsWith("HTTP", StringComparison.Ordinal) && !ct.IsCancellationRequested)
        {
            var g2 = await GetAsync(reqUrl, 25, local, 2048, ct, headers: local ? null : headers).ConfigureAwait(false);
            if (g2.ok && g2.bytes > 0)
            {
                g = g2;
                g.error = null;
            }
        }

        sw.Stop();
        NoteHost(res, reqUrl, local, "segment", g.ok && g.bytes > 0, g.error ?? (g.ok ? "пустой ответ" : null));

        if (!g.ok || g.bytes == 0)
        {
            res.steps.Add(new Step { name = "segment", ok = false, ms = sw.ElapsedMilliseconds, detail = g.error ?? "пустой ответ" });
            res.status = "fail";
            res.failedAt = "segment";
            return res;
        }

        res.steps.Add(new Step { name = "segment", ok = true, ms = sw.ElapsedMilliseconds, detail = $"прочитано {g.bytes} байт" });
        res.status = "ok";
        return res;
    }
    #endregion

    #region HTTP и разбор адресов
    static Dictionary<string, string> ReadHeaders(JObject o)
    {
        try
        {
            var h = o?["headers"] as JObject;
            if (h == null || h.Count == 0)
                return null;

            var d = new Dictionary<string, string>();
            foreach (var p in h.Properties())
            {
                if (p.Value.Type == JTokenType.String)
                    d[p.Name] = p.Value.ToString();
            }

            return d.Count > 0 ? d : null;
        }
        catch
        {
            return null;
        }
    }

    static readonly Regex RjsonArg = new(@"([?&])rjson=[^&]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ссылки провайдеров на следующий шаг часто без rjson: тогда Lampac отвечает HTML, а не JSON
    static string WithRjson(string url)
    {
        if (RjsonArg.IsMatch(url))
            return RjsonArg.Replace(url, "$1rjson=true");
        return url + (url.Contains('?') ? "&" : "?") + "rjson=true";
    }

    static string LocalBase() => $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}";

    static readonly string[] LocalPrefixes = { "/lite/", "/proxy/", "/proxy-dash/", "/proxy", "/transcoding", "/tmdb/", "/cub/" };

    /// <summary>Если адрес указывает на сам Lampac (по пути), переписывает его на localhost. Чужие адреса не трогает.</summary>
    static bool TryLocalize(string url, out string local)
    {
        local = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string path;
        string rest;
        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            path = url;
            rest = url;
        }
        else
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
                return false;

            // адрес Lampac определяем по порту и пути: хост за обратным прокси может быть любым
            path = u.AbsolutePath;
            rest = u.PathAndQuery;
            if (u.Port != CoreInit.conf.listen.port && !u.Host.Equals(CoreInit.conf.listen.localhost, StringComparison.OrdinalIgnoreCase) && !IsOwnHost(u))
                return false;
        }

        if (!LocalPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        local = LocalBase() + rest;
        return true;
    }

    static bool IsOwnHost(Uri u) => u.IsLoopback;

    /// <summary>Ссылка на эндпоинт провайдера (/lite/...): хост игнорируем, всегда идём на localhost.</summary>
    static bool TryLocalizeLite(string url, out string local)
    {
        local = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        string rest;
        if (url.StartsWith("/", StringComparison.Ordinal))
            rest = url;
        else if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
            rest = u.PathAndQuery;
        else
            return false;

        if (!rest.StartsWith("/lite/", StringComparison.OrdinalIgnoreCase))
            return false;

        local = LocalBase() + rest;
        return true;
    }

    static string ToAbsolute(string link, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(link))
            return null;

        if (Uri.TryCreate(link, UriKind.Absolute, out var a) && (a.Scheme == "http" || a.Scheme == "https"))
            return a.ToString();

        if (link.StartsWith("/", StringComparison.Ordinal) && baseUrl == null)
            return LocalBase() + link;

        if (baseUrl != null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, link, out var r))
            return r.ToString();

        return null;
    }

    /// <summary>Первая строка-ссылка после тега (для master - после EXT-X-STREAM-INF, для media - после EXTINF).</summary>
    static string FirstUri(string m3u8, string afterTag)
    {
        bool seen = false;
        foreach (var raw in m3u8.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith(afterTag, StringComparison.Ordinal))
            {
                seen = true;
                continue;
            }

            if (seen && !line.StartsWith("#", StringComparison.Ordinal))
                return line;
        }

        return null;
    }

    sealed class GetResult
    {
        public bool ok;
        public string error;
        public string body;
        public string text;
        public int bytes;
        public string contentType;
        public string finalUrl;
    }

    static async Task<GetResult> GetAsync(string url, int timeoutSeconds, bool withPasswd, int maxBytes, CancellationToken ct = default, string range = null, IReadOnlyDictionary<string, string> headers = null)
    {
        var r = new GetResult();

        CancellationTokenSource cts = null;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (withPasswd && !string.IsNullOrEmpty(CoreInit.rootPasswd))
                req.Headers.TryAddWithoutValidation("lcrqpasswd", CoreInit.rootPasswd);
            if (headers != null)
            {
                // заголовки, которые провайдер требует для прямой ссылки (Referer, User-Agent и т.п.): плеер отправляет их же
                foreach (var h in headers)
                {
                    if (!string.IsNullOrEmpty(h.Key) && h.Value != null)
                        req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            if (range != null)
                req.Headers.TryAddWithoutValidation("Range", range);

            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            r.contentType = resp.Content.Headers.ContentType?.MediaType;
            r.finalUrl = resp.RequestMessage?.RequestUri?.ToString();

            if (code < 200 || code >= 300)
            {
                r.error = "HTTP " + code;

                if (withPasswd)
                {
                    // OnError() кладёт причину в тело ответа: показываем её, а не только код
                    try
                    {
                        await using var es = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                        var eb = new byte[600];
                        int en = 0;
                        while (en < eb.Length)
                        {
                            int n = await es.ReadAsync(eb.AsMemory(en, eb.Length - en), cts.Token).ConfigureAwait(false);
                            if (n <= 0)
                                break;
                            en += n;
                        }

                        string t = System.Text.RegularExpressions.Regex.Replace(Encoding.UTF8.GetString(eb, 0, en), "<[^>]*>", " ");
                        t = System.Text.RegularExpressions.Regex.Replace(t, "\\s+", " ").Trim();
                        if (t.Length > 0)
                            r.body = Clip(t, 200);
                    }
                    catch
                    {
                    }
                }

                return r;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new byte[maxBytes];
            int total = 0;
            while (total < buf.Length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), cts.Token).ConfigureAwait(false);
                if (n <= 0)
                    break;
                total += n;
            }

            r.bytes = total;
            r.text = Encoding.UTF8.GetString(buf, 0, total);
            r.ok = true;
            return r;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                r.error = "отменено";
            else if (cts != null && cts.IsCancellationRequested)
                r.error = $"нет ответа за {timeoutSeconds} с";
            else
                r.error = "не удалось подключиться за 10 с (connect timeout)";
            return r;
        }
        catch (Exception ex)
        {
            r.error = Clip(ex.InnerException?.Message ?? ex.Message, 160);
            return r;
        }
        finally
        {
            cts?.Dispose();
        }
    }
    #endregion

    #region домены и IP для маршрутизации
    static readonly Regex HostName = new(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?$", RegexOptions.Compiled);

    static string CleanHost(string h) => h?.Trim().Trim('[', ']').ToLowerInvariant();

    /// <summary>Хост, на который реально уйдёт запрос: для /proxy/{токен} достаём исходный адрес из токена, для внешних ссылок берём хост ссылки.</summary>
    static string HostOfRequest(string reqUrl, bool local, out bool upstreamProxy)
    {
        upstreamProxy = false;
        try
        {
            if (!Uri.TryCreate(reqUrl, UriKind.Absolute, out var u))
                return null;

            if (!local)
                return CleanHost(u.Host);

            string path = Uri.UnescapeDataString(u.AbsolutePath);
            string hash;
            if (path.StartsWith("/proxy-dash/", StringComparison.OrdinalIgnoreCase))
            {
                hash = path.Substring(12);
                if (hash.Length < 32)
                    return null;
                hash = hash.Substring(0, 32);
            }
            else if (path.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase))
            {
                hash = path.Substring(7);
            }
            else
            {
                return null;
            }

            var link = ProxyLink.Decrypt(hash.AsSpan(), null);
            if (link?.uri == null || !Uri.TryCreate(link.uri, UriKind.Absolute, out var real))
                return null;

            upstreamProxy = link.proxy != null;
            return CleanHost(real.Host);
        }
        catch
        {
            return null;
        }
    }

    static void NoteHost(CheckResult res, string reqUrl, bool local, string role, bool ok, string error)
    {
        string host = HostOfRequest(reqUrl, local, out bool up);
        if (string.IsNullOrEmpty(host))
            return;

        var ex = res.hosts.FirstOrDefault(h => h.host == host && h.role == role);
        if (ex == null)
        {
            res.hosts.Add(new HostInfo { host = host, role = role, ok = ok, error = ok ? null : error, upstreamProxy = up });
        }
        else if (!ok)
        {
            ex.ok = false;
            ex.error ??= error;
        }
    }

    static async Task ResolveHostsAsync(List<HostInfo> hosts, CancellationToken ct)
    {
        var cache = new Dictionary<string, string[]>();
        foreach (var h in hosts)
        {
            if (!cache.TryGetValue(h.host, out var ips))
            {
                ips = await ResolveOneAsync(h.host, ct).ConfigureAwait(false);
                cache[h.host] = ips;
            }

            h.ips = ips;
        }
    }

    // IP определяются DNS-резолвером сервера Lampac: резолвер роутера или VPN может отдать другие адреса
    static async Task<string[]> ResolveOneAsync(string host, CancellationToken ct, int timeoutSeconds = 3)
    {
        if (IPAddress.TryParse(host, out var literal))
            return new[] { literal.ToString() };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return addrs.Select(a => a.ToString()).Distinct().Take(8).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public sealed class ProbeRequest
    {
        public List<string> hosts { get; set; }
    }

    sealed class FamilyProbe
    {
        public bool reachable { get; set; }
        // ok | timeout | unreachable | refused | reset | tls | dns | skip | error
        public string kind { get; set; } = "error";
        public int code { get; set; }
        public long ms { get; set; }
        public string detail { get; set; }
    }

    sealed class ProbeResult
    {
        public string host { get; set; }
        public bool reachable { get; set; }      // работает хотя бы одно семейство адресов
        public bool partial { get; set; }        // одно семейство работает, другое нет
        public string kind { get; set; } = "error";
        public int code { get; set; }
        public long ms { get; set; }
        public string detail { get; set; }
        public string[] ips { get; set; } = Array.Empty<string>();
        public FamilyProbe v4 { get; set; }
        public FamilyProbe v6 { get; set; }
    }

    static HttpClient MakeProbeClient(AddressFamily family) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(6),
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        // подключаемся адресами только одного семейства: так видно, какой из IPv4/IPv6 работает
        ConnectCallback = async (ctx, token) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, family, cts.Token).ConfigureAwait(false);
                if (addrs.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, cts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                socket.Dispose();
                throw new SocketException((int)SocketError.TimedOut);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static readonly HttpClient ProbeClient4 = MakeProbeClient(AddressFamily.InterNetwork);
    static readonly HttpClient ProbeClient6 = MakeProbeClient(AddressFamily.InterNetworkV6);

    // есть ли у Lampac (в его контейнере) глобальный IPv6: без него проверять IPv6-адреса бессмысленно
    static bool ServerHasIpv6()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && n.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetworkV6
                    && !a.Address.IsIPv6LinkLocal
                    && !IPAddress.IsLoopback(a.Address)));
        }
        catch
        {
            return false;
        }
    }

    // адреса, к которым не подключаемся: пустой DNS-ответ (0.0.0.0), локальные и частные сети
    static bool IsSpecialIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;

        var b = ip.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    static void Classify(Exception ex, CancellationToken ct, FamilyProbe f)
    {
        if (ex is OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                f.kind = "error";
                f.detail = "отменено";
            }
            else
            {
                f.kind = "timeout";
                f.detail = "нет соединения за 6-10 с";
            }

            return;
        }

        SocketException se = null;
        bool tls = (ex as HttpRequestException)?.HttpRequestError == HttpRequestError.SecureConnectionError;
        for (var e = ex; e != null; e = e.InnerException)
        {
            se ??= e as SocketException;
            tls |= e is System.Security.Authentication.AuthenticationException;
        }

        if (se != null)
        {
            f.kind = se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "refused",
                SocketError.TimedOut => "timeout",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "unreachable",
                SocketError.ConnectionReset => "reset",
                SocketError.HostNotFound or SocketError.TryAgain => "dns",
                _ => "error"
            };
            f.detail = f.kind == "timeout" ? "нет соединения за 6-10 с" : se.SocketErrorCode.ToString();
        }
        else if (tls)
        {
            string msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Contains("remote certificate", StringComparison.OrdinalIgnoreCase))
            {
                // рукопожатие прошло, не прошла только проверка сертификата (чужое имя/самоподписанный): сеть до узла работает
                f.kind = "cert";
                f.detail = Clip(msg, 120);
            }
            else
            {
                // TLS оборвался до сертификата: типичный признак фильтрации по SNI
                f.kind = "tls";
                f.detail = Clip(msg, 120);
            }
        }
        else
        {
            f.kind = "error";
            f.detail = Clip(ex.InnerException?.Message ?? ex.Message, 120);
        }
    }

    static async Task<FamilyProbe> ProbeFamilyAsync(string host, AddressFamily family, CancellationToken ct)
    {
        var f = await ProbeFamilyOnceAsync(host, family, ct).ConfigureAwait(false);

        // единичный сбой соединения не считаем недоступностью: повторяем один раз
        if (!f.reachable && (f.kind is "timeout" or "reset" or "unreachable") && !ct.IsCancellationRequested)
        {
            await Task.Delay(400, ct).ConfigureAwait(false);
            var again = await ProbeFamilyOnceAsync(host, family, ct).ConfigureAwait(false);
            if (again.reachable)
            {
                again.detail = (again.detail != null ? again.detail + "; " : "") + "со 2-й попытки";
                return again;
            }
        }

        return f;
    }

    static async Task<FamilyProbe> ProbeFamilyOnceAsync(string host, AddressFamily family, CancellationToken ct)
    {
        var f = new FamilyProbe();
        var sw = Stopwatch.StartNew();
        try
        {
            string target = family == AddressFamily.InterNetworkV6 && IPAddress.TryParse(host, out _) ? "[" + host + "]" : host;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://" + target + "/");
            using var resp = await (family == AddressFamily.InterNetworkV6 ? ProbeClient6 : ProbeClient4)
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            f.reachable = true;
            f.kind = "ok";
            f.code = (int)resp.StatusCode;
        }
        catch (Exception ex)
        {
            Classify(ex, ct, f);
            f.reachable = f.kind == "cert";
        }

        f.ms = sw.ElapsedMilliseconds;
        return f;
    }

    /// <summary>Что видит сервер Lampac: DNS, затем TLS-соединение на 443 отдельно по IPv4 и IPv6 и любой HTTP-ответ (код не важен: важно, что соединение есть).</summary>
    static async Task<ProbeResult> ProbeOneAsync(string host, bool serverIpv6, CancellationToken ct)
    {
        var r = new ProbeResult { host = host };
        r.ips = await ResolveOneAsync(host, ct).ConfigureAwait(false);
        if (r.ips.Length == 0 && !ct.IsCancellationRequested)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            r.ips = await ResolveOneAsync(host, ct, 5).ConfigureAwait(false);
        }

        if (r.ips.Length == 0)
        {
            r.kind = "dns";
            r.detail = "DNS не вернул адресов";
            return r;
        }

        var special = r.ips.FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && IsSpecialIp(ip));
        if (special != null)
        {
            r.kind = "dns";
            r.detail = "резолвится в служебный адрес " + special + " (похоже на блокировку DNS)";
            return r;
        }

        bool has4 = r.ips.Any(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);
        bool has6 = r.ips.Any(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6);

        var t4 = has4 ? ProbeFamilyAsync(host, AddressFamily.InterNetwork, ct) : null;
        var t6 = has6 && serverIpv6 ? ProbeFamilyAsync(host, AddressFamily.InterNetworkV6, ct) : null;

        if (t4 != null)
            r.v4 = await t4.ConfigureAwait(false);

        if (t6 != null)
            r.v6 = await t6.ConfigureAwait(false);
        else if (has6)
            r.v6 = new FamilyProbe { kind = "skip", detail = "у сервера нет IPv6" };

        var done = new[] { r.v4, r.v6 }.Where(x => x != null && x.kind != "skip").ToList();
        if (done.Count == 0)
        {
            r.kind = "unreachable";
            r.detail = "у домена только IPv6-адреса, а у сервера нет IPv6";
            return r;
        }

        var best = done.FirstOrDefault(x => x.reachable) ?? done[0];
        r.reachable = done.Any(x => x.reachable);
        r.partial = r.reachable && done.Any(x => !x.reachable);
        r.kind = best.kind;
        r.code = best.code;
        r.ms = best.ms;
        r.detail = best.detail;
        return r;
    }

    /// <summary>Проверка доступности доменов с сервера Lampac. Адреса берутся из настроек и ссылок провайдеров; только админ панели, частные адреса не опрашиваются.</summary>
    [HttpPost]
    [Route("/adminpanel/api/healthcheck/probe")]
    public async Task<ActionResult> Probe()
    {
        string body = await ReadBodyAsync().ConfigureAwait(false);
        ProbeRequest req;
        try
        {
            req = JsonConvert.DeserializeObject<ProbeRequest>(body ?? string.Empty);
        }
        catch (JsonException ex)
        {
            return Err(400, "invalid json", ex.Message);
        }

        var list = (req?.hosts ?? new List<string>())
            .Select(CleanHost)
            .Where(h => !string.IsNullOrEmpty(h) && (IPAddress.TryParse(h, out _) || HostName.IsMatch(h)))
            .Distinct()
            .Take(60)
            .ToList();

        bool serverIpv6 = ServerHasIpv6();
        var gate = new SemaphoreSlim(8, 8);
        var tasks = list.Select(async h =>
        {
            await gate.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            try
            {
                return await ProbeOneAsync(h, serverIpv6, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return Json(new { probe = results, serverIpv6 });
    }
    #endregion

    #region утилиты
    async Task<Sample> FindSampleAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var (list, _) = await LoadSamplesAsync().ConfigureAwait(false);
        return list.FirstOrDefault(s => s.id == id);
    }

    async Task<string> ReadBodyAsync()
    {
        using var sr = new StreamReader(Request.Body, Encoding.UTF8);
        return await sr.ReadToEndAsync().ConfigureAwait(false);
    }

    ActionResult Err(int status, string error, string detail = null)
    {
        return new ContentResult
        {
            StatusCode = status,
            ContentType = "application/json; charset=utf-8",
            Content = JsonConvert.SerializeObject(new { ok = false, error, detail })
        };
    }

    static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        return s.Length <= max ? s : s.Substring(0, max);
    }
    #endregion
}
