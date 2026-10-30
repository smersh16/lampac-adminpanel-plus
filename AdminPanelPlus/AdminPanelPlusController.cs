using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Shared;
using Shared.Attributes;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AdminPanelPlus;

[Authorization(redirectUri: "/adminpanel/auth")]
public class AdminPanelPlusController : BaseController
{
    const string InitFile = "init.conf";
    const string CurrentFile = "current.conf";
    const string UsersFile = "users.json";

    // Копии init.conf/users.json перед каждой записью из панели.
    // Не путать с database/backup/init/ — там ядро хранит слитый current-конфиг, по одному файлу в час.
    const string BackupDir = "database/backup/adminpanel";
    const int BackupKeep = 30;
    static readonly Regex BackupName = new(@"^(init|users)-\d{8}-\d{6}-\d{3}\.(conf|json)$", RegexOptions.Compiled);

    static readonly JsonSerializerSettings CamelSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore
    };

    [HttpGet]
    [AllowAnonymous]
    [Route("/adminpanel/auth")]
    public ActionResult Auth()
    {
        var path = Path.Combine(ModInit.modpath, "auth.html");
        var html = System.IO.File.ReadAllText(path, Encoding.UTF8);
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet]
    [Route("/adminpanel")]
    public ActionResult Index()
    {
        var path = Path.Combine(ModInit.modpath, "index.html");
        var html = System.IO.File.ReadAllText(path, Encoding.UTF8);
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet]
    [Route("/adminpanel/api/groups")]
    public ActionResult Groups()
    {
        var built = ConfigSectionGroups.Build(LoadCurrentRoot());
        return Content(JsonConvert.SerializeObject(built, CamelSettings), "application/json; charset=utf-8");
    }

    [HttpGet]
    [Route("/adminpanel/api/groups/catalog")]
    public ActionResult GroupsCatalog()
    {
        var built = ConfigSectionGroups.BuildCatalog();
        var current = LoadCurrentRoot();
        var inCatalog = ConfigSectionGroups.CatalogRootKeys;
        var orphans = current.Properties()
            .Select(p => p.Name)
            .Where(k => !inCatalog.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        if (orphans.Length > 0)
            built.Add(new GroupDto("other", "Прочее", "Ключи из current, не из каталога групп.", orphans));

        return Content(JsonConvert.SerializeObject(built, CamelSettings), "application/json; charset=utf-8");
    }

    // Подсказки к параметрам: hints.json лежит рядом с index.html.
    [HttpGet]
    [Route("/adminpanel/api/hints")]
    public ActionResult Hints()
    {
        var path = Path.Combine(ModInit.modpath, "hints.json");
        if (!System.IO.File.Exists(path))
            return Content("{}", "application/json; charset=utf-8");

        return Content(System.IO.File.ReadAllText(path, Encoding.UTF8), "application/json; charset=utf-8");
    }

    static JObject LoadCurrentRoot()
    {
        try
        {
            if (System.IO.File.Exists(CurrentFile))
                return JObject.Parse(System.IO.File.ReadAllText(CurrentFile, Encoding.UTF8));
        }
        catch (JsonException)
        {
        }

        if (CoreInit.CurrentConf != null)
        {
            try
            {
                return (JObject)CoreInit.CurrentConf.DeepClone();
            }
            catch
            {
            }
        }

        return new JObject();
    }

    [HttpGet]
    [Route("/adminpanel/api/init")]
    public ActionResult GetInit()
    {
        if (!System.IO.File.Exists(InitFile))
            return Content("{}", "application/json; charset=utf-8");

        var text = System.IO.File.ReadAllText(InitFile, Encoding.UTF8);
        return Content(NormalizeJsonText(text), "application/json; charset=utf-8");
    }

    [HttpGet]
    [Route("/adminpanel/api/current")]
    public ActionResult GetCurrent()
    {
        if (!System.IO.File.Exists(CurrentFile))
        {
            if (CoreInit.CurrentConf != null)
                return Content(CoreInit.CurrentConf.ToString(Formatting.Indented), "application/json; charset=utf-8");
            return Content("{}", "application/json; charset=utf-8");
        }

        var text = System.IO.File.ReadAllText(CurrentFile, Encoding.UTF8);
        return Content(NormalizeJsonText(text), "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/adminpanel/api/init")]
    public async Task<IActionResult> SaveInit()
    {
        var body = await ReadBodyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return AdminJsonError(400, "empty body");

        try
        {
            var parsed = JToken.Parse(body);
            if (parsed.Type != JTokenType.Object)
                return AdminJsonError(400, "root must be a JSON object");

            var backup = await WriteAtomicAsync(InitFile, ((JObject)parsed).ToString(Formatting.Indented)).ConfigureAwait(false);
            return AdminJsonOk(backup);
        }
        catch (JsonException ex)
        {
            return AdminJsonError(400, "invalid json", ex.Message);
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to write init.conf", ex.Message);
        }
    }

    [HttpPost]
    [Route("/adminpanel/api/init/section/{key}")]
    public async Task<IActionResult> SaveInitSection(string key)
    {
        if (!IsValidKey(key))
            return AdminJsonError(400, "invalid section key");

        var body = await ReadBodyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return AdminJsonError(400, "empty body");

        JToken sectionToken;
        try
        {
            sectionToken = JToken.Parse(body);
        }
        catch (JsonException ex)
        {
            return AdminJsonError(400, "invalid json", ex.Message);
        }

        var root = ReadInitRoot();
        if (root == null)
            return AdminJsonError(400, "init.conf is not valid json", "fix it on the init.conf tab first");

        try
        {
            root[key] = sectionToken.DeepClone();
            var backup = await WriteAtomicAsync(InitFile, root.ToString(Formatting.Indented)).ConfigureAwait(false);
            return AdminJsonOk(backup);
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to write init.conf", ex.Message);
        }
    }

    // Убрать корневой ключ из init.conf: дальше действуют значения из base.conf и умолчания ядра.
    [HttpPost]
    [Route("/adminpanel/api/init/section/{key}/reset")]
    public async Task<IActionResult> ResetInitSection(string key)
    {
        if (!IsValidKey(key))
            return AdminJsonError(400, "invalid section key");

        var root = ReadInitRoot();
        if (root == null)
            return AdminJsonError(400, "init.conf is not valid json", "fix it on the init.conf tab first");

        if (!root.Remove(key))
            return AdminJsonOk();

        try
        {
            var backup = await WriteAtomicAsync(InitFile, root.ToString(Formatting.Indented)).ConfigureAwait(false);
            return AdminJsonOk(backup);
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to write init.conf", ex.Message);
        }
    }

    [HttpGet]
    [Route("/adminpanel/api/users-json")]
    public ActionResult GetUsersJson()
    {
        if (!System.IO.File.Exists(UsersFile))
            return Content("[]", "application/json; charset=utf-8");

        var text = System.IO.File.ReadAllText(UsersFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text))
            return Content("[]", "application/json; charset=utf-8");

        return Content(NormalizeJsonText(text), "application/json; charset=utf-8");
    }

    [HttpPost]
    [Route("/adminpanel/api/users-json")]
    public async Task<IActionResult> SaveUsersJson()
    {
        var body = await ReadBodyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return AdminJsonError(400, "empty body");

        try
        {
            var parsed = JToken.Parse(body);
            if (parsed.Type != JTokenType.Array)
                return AdminJsonError(400, "root must be a JSON array", "users.json must be a list of AccsUser objects");

            foreach (var item in (JArray)parsed)
            {
                if (item.Type != JTokenType.Object)
                    return AdminJsonError(400, "invalid array item", "each element must be a JSON object");
            }

            var backup = await WriteAtomicAsync(UsersFile, ((JArray)parsed).ToString(Formatting.Indented)).ConfigureAwait(false);
            return AdminJsonOk(backup);
        }
        catch (JsonException ex)
        {
            return AdminJsonError(400, "invalid json", ex.Message);
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to write users.json", ex.Message);
        }
    }

    #region backups
    [HttpGet]
    [Route("/adminpanel/api/backups")]
    public ActionResult Backups()
    {
        var list = new JArray();

        if (Directory.Exists(BackupDir))
        {
            var files = Directory.GetFiles(BackupDir)
                .Select(p => new FileInfo(p))
                .Where(fi => BackupName.IsMatch(fi.Name))
                .OrderByDescending(fi => fi.Name, StringComparer.Ordinal);

            foreach (var fi in files)
            {
                list.Add(new JObject
                {
                    ["name"] = fi.Name,
                    ["file"] = fi.Name.StartsWith("init-", StringComparison.Ordinal) ? InitFile : UsersFile,
                    ["size"] = fi.Length,
                    ["utc"] = fi.LastWriteTimeUtc.ToString("o")
                });
            }
        }

        return Content(list.ToString(Formatting.None), "application/json; charset=utf-8");
    }

    // Тело: {"name":"init-20261030-083000-123.conf"}. Текущий файл перед заменой тоже сохраняется в копию.
    [HttpPost]
    [Route("/adminpanel/api/backups/restore")]
    public async Task<IActionResult> RestoreBackup()
    {
        var body = await ReadBodyAsync().ConfigureAwait(false);

        string name;
        try
        {
            name = (string)JObject.Parse(body)["name"];
        }
        catch (Exception)
        {
            return AdminJsonError(400, "invalid json");
        }

        // имя проверено регуляркой, поэтому выйти из BackupDir нельзя
        if (name == null || !BackupName.IsMatch(name))
            return AdminJsonError(400, "invalid backup name");

        var path = Path.Combine(BackupDir, name);
        if (!System.IO.File.Exists(path))
            return AdminJsonError(404, "backup not found");

        var isInit = name.StartsWith("init-", StringComparison.Ordinal);

        try
        {
            var parsed = JToken.Parse(await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false));
            if (parsed.Type != (isInit ? JTokenType.Object : JTokenType.Array))
                return AdminJsonError(400, "unexpected backup content");

            var backup = await WriteAtomicAsync(isInit ? InitFile : UsersFile, parsed.ToString(Formatting.Indented)).ConfigureAwait(false);
            return AdminJsonOk(backup);
        }
        catch (JsonException ex)
        {
            return AdminJsonError(400, "backup is not valid json", ex.Message);
        }
        catch (IOException ex)
        {
            return AdminJsonError(500, "failed to restore backup", ex.Message);
        }
    }

    // Копия файла перед записью. Возвращает имя копии или null: файла нет, копия не нужна
    // (совпадает с последней) либо не удалось создать — запись при этом не блокируется.
    static string BackupFile(string file)
    {
        try
        {
            if (!System.IO.File.Exists(file))
                return null;

            Directory.CreateDirectory(BackupDir);

            var stem = Path.GetFileNameWithoutExtension(file);
            var ext = Path.GetExtension(file);
            var existing = Directory.GetFiles(BackupDir, stem + "-*" + ext)
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .ToArray();

            var text = System.IO.File.ReadAllText(file, Encoding.UTF8);
            if (existing.Length > 0 && System.IO.File.ReadAllText(existing[0], Encoding.UTF8) == text)
                return null;

            var name = $"{stem}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}{ext}";
            System.IO.File.WriteAllText(Path.Combine(BackupDir, name), text, Encoding.UTF8);

            foreach (var old in existing.Skip(BackupKeep - 1))
                System.IO.File.Delete(old);

            return name;
        }
        catch
        {
            return null;
        }
    }
    #endregion

    // Все записи конфигов идут через этот метод, поэтому копия делается ровно в одном месте.
    // tmp+Move — атомарно; на bind mount Move(..., overwrite) часто EBUSY → пишем в файл напрямую.
    static async Task<string> WriteAtomicAsync(string file, string formatted)
    {
        var backup = BackupFile(file);

        var tmp = file + ".tmp";
        await System.IO.File.WriteAllTextAsync(tmp, formatted, Encoding.UTF8).ConfigureAwait(false);
        try
        {
            try
            {
                System.IO.File.Move(tmp, file, overwrite: true);
            }
            catch (IOException ex) when (IsReplaceTargetBusy(ex))
            {
                await System.IO.File.WriteAllTextAsync(file, formatted, Encoding.UTF8).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tmp))
                    System.IO.File.Delete(tmp);
            }
            catch
            {
            }
        }

        return backup;
    }

    static bool IsReplaceTargetBusy(IOException ex)
    {
        for (Exception e = ex; e != null; e = e.InnerException)
        {
            if (e.Message != null && e.Message.Contains("busy", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // init.conf как объект: пустой, если файла нет или он пустой.
    // null — файл есть, но это не JSON: его не затираем, пока пользователь не поправит его вручную.
    static JObject ReadInitRoot()
    {
        if (!System.IO.File.Exists(InitFile))
            return new JObject();

        var text = System.IO.File.ReadAllText(InitFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(text))
            return new JObject();

        try
        {
            return JObject.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool IsValidKey(string key)
    {
        return !string.IsNullOrWhiteSpace(key) && !key.Contains('/') && !key.Contains('\\');
    }

    async Task<string> ReadBodyAsync()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    static ContentResult AdminJsonOk(string backup = null)
    {
        var o = new JObject { ["ok"] = true };
        if (backup != null)
            o["backup"] = backup;

        return new ContentResult
        {
            Content = o.ToString(Formatting.None),
            ContentType = "application/json; charset=utf-8",
            StatusCode = 200
        };
    }

    static ContentResult AdminJsonError(int status, string error, string detail = null)
    {
        var o = new JObject { ["error"] = error };
        if (!string.IsNullOrEmpty(detail))
            o["detail"] = detail;
        return new ContentResult
        {
            Content = o.ToString(Formatting.None),
            ContentType = "application/json; charset=utf-8",
            StatusCode = status
        };
    }

    static string NormalizeJsonText(string raw)
    {
        try
        {
            return JToken.Parse(raw).ToString(Formatting.Indented);
        }
        catch
        {
            return raw;
        }
    }
}
