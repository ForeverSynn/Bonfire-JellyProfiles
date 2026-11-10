using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// Patching index.html on disk under a Base URL (CodeRabbit on ForeverSynn PR #1).
//
// The middleware already wrote the Base URL into the script tag; the file patch did not.
// Under a Base URL (/jellyfin) a bare /plugins/profiles/profiles.js is redirected to the
// web page, so in "file" mode the switcher, gate and theme never loaded, and the status
// check called the wrong tag current because it compared the version only.
//
// Drives the REAL ProfilesBootstrapTask.TryPatchIndex against a temporary web folder.
// Against the build before the fix, the prefixed cases fail.
//
// Usage: dotnet run -c Release   (loads bin/Release/<tfm>/Jellyfin.Profiles.dll)
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0;
var fails = new List<string>();
void Ok(string name, bool cond, string detail = null)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + name); }
    else { fails.Add(name + (detail is null ? "" : "  — " + detail)); Console.WriteLine("  FAIL  " + name + (detail is null ? "" : "  — " + detail)); }
}

static string RepoRoot()
{
    var d = AppContext.BaseDirectory;
    while (d != null && !File.Exists(Path.Combine(d, "Jellyfin.Profiles.csproj")))
        d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    return d ?? throw new Exception("could not find the repository root");
}

var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
var asm = Assembly.LoadFrom(Path.Combine(RepoRoot(), "bin", "Release", tfm, "Jellyfin.Profiles.dll"));
var pluginType = asm.GetType("Jellyfin.Profiles.Plugin", true);
var cfgType = asm.GetType("Jellyfin.Profiles.Configuration.PluginConfiguration", true);
var taskType = asm.GetType("Jellyfin.Profiles.ProfilesBootstrapTask", true);
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

var web = Path.Combine(Path.GetTempPath(), "bonfire-basefile-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(web);
var index = Path.Combine(web, "index.html");
const string Shipped = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><div id=\"reactRoot\"></div>\n</body></html>";

var plugin = Activator.CreateInstance(pluginType, new StubPaths(web), new StubXml());
var config = pluginType.GetProperty("Configuration").GetValue(plugin);
cfgType.GetProperty("IndexInjectionMode").SetValue(config, "file");

var loggerType = typeof(NullLogger<>).MakeGenericType(taskType);
var logger = loggerType.GetField("Instance", Any)?.GetValue(null)
    ?? loggerType.GetProperty("Instance", Any)?.GetValue(null)
    ?? Activator.CreateInstance(loggerType, nonPublic: true);
var serverConfig = ServerConfigStub.Create();
var task = Activator.CreateInstance(taskType, new StubPaths(web), (IUserManager)null, logger, serverConfig);
var patch = taskType.GetMethod("TryPatchIndex", Any);
var refresh = taskType.GetMethod("RefreshInjectionStatus", Any);
bool Stale() => (bool)taskType.GetProperty("IsVersionStale", Any).GetValue(null);
bool RestartRequired() => taskType.GetProperty("RestartRequired", Any)?.GetValue(null) is true;
Ok("the bootstrap task takes the server configuration", task != null && patch != null);

string Run(string baseUrl, string start)
{
    ServerConfigStub.BaseUrl = baseUrl;
    File.WriteAllText(index, start);
    taskType.GetProperty("IndexPath", Any)?.SetValue(null, null);
    patch.Invoke(task, null);
    return File.ReadAllText(index);
}
int Tags(string html) => Regex.Matches(html, @"/plugins/profiles/profiles\.js").Count;
string Src(string html) => Regex.Match(html, @"src=""([^""]*/plugins/profiles/profiles\.js)").Groups[1].Value;

Console.WriteLine("-- Under a Base URL --");
var html = Run("/jellyfin", Shipped);
Ok("a fresh index.html gets the tag with the Base URL", Src(html) == "/jellyfin/plugins/profiles/profiles.js", Src(html));
Ok("and the anti-flicker script", html.Contains("jpf-eh"));

var bare = Run("", Shipped);
Ok("without a Base URL the tag stays bare", Src(bare) == "/plugins/profiles/profiles.js", Src(bare));

html = Run("/jellyfin", bare);
Ok("a bare tag of the current build is rewritten with the Base URL", Src(html) == "/jellyfin/plugins/profiles/profiles.js", Src(html));
Ok("leaving one tag, not two", Tags(html) == 1, Tags(html).ToString());

html = Run("jellyfin/", bare);
Ok("the Base URL is normalised as Jellyfin does (jellyfin/ is /jellyfin)", Src(html) == "/jellyfin/plugins/profiles/profiles.js", Src(html));

var prefixed = html;
html = Run("", prefixed);
Ok("removing the Base URL takes the prefix back out", Src(html) == "/plugins/profiles/profiles.js", Src(html));

html = Run("/media", prefixed);
Ok("a changed Base URL replaces the old prefix", Src(html) == "/media/plugins/profiles/profiles.js" && Tags(html) == 1, Src(html));

var before = Run("/jellyfin", prefixed);
var mtime = File.GetLastWriteTimeUtc(index);
System.Threading.Thread.Sleep(20);
patch.Invoke(task, null);
Ok("an already-correct file is left alone", File.ReadAllText(index) == before && File.GetLastWriteTimeUtc(index) == mtime);

ServerConfigStub.Throw = true;
html = Run("/jellyfin", Shipped);
Ok("a Base URL that cannot be read counts as none", Src(html) == "/plugins/profiles/profiles.js", Src(html));
ServerConfigStub.Throw = false;

Console.WriteLine();
Console.WriteLine("-- The dashboard status check --");
// As after a normal start: the pipeline hook was registered at host startup.
asm.GetType("Jellyfin.Profiles.ProfilesIndexMiddleware", true).GetProperty("IsRegistered", Any)?.SetValue(null, true);
if (refresh != null && !RestartRequired())
{
    ServerConfigStub.BaseUrl = "/jellyfin";
    File.WriteAllText(index, bare);
    refresh.Invoke(null, null);
    Ok("a bare tag under a Base URL reads as stale, not current", Stale());
    File.WriteAllText(index, prefixed);
    refresh.Invoke(null, null);
    Ok("the prefixed tag reads as current", !Stale());
}
else
{
    Console.WriteLine("  (skipped: the status check reports a pending restart in this harness)");
}

try { Directory.Delete(web, true); } catch { }
Console.WriteLine();
if (fails.Count > 0)
{
    foreach (var f in fails) Console.WriteLine("   - " + f);
    Console.WriteLine(pass + " passed, " + fails.Count + " failed");
    return 1;
}
Console.WriteLine(pass + " passed, 0 failed");
return 0;

// IServerConfigurationManager, for GetNetworkConfiguration() only: that extension reads
// GetConfiguration("network").
public class ServerConfigStub : DispatchProxy
{
    public static string BaseUrl = string.Empty;
    public static bool Throw;

    public static IServerConfigurationManager Create() => DispatchProxy.Create<IServerConfigurationManager, ServerConfigStub>();

    protected override object Invoke(MethodInfo m, object[] args)
    {
        if (m.Name == "GetConfiguration")
        {
            if (Throw) throw new InvalidOperationException("network.xml unreadable");
            return new NetworkConfiguration { BaseUrl = BaseUrl };
        }
        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }
}

sealed class StubPaths : MediaBrowser.Common.Configuration.IApplicationPaths
{
    private readonly string _root;
    public StubPaths(string root) { _root = root; }
    public string ProgramDataPath => _root;
    public string WebPath => _root;
    public string ProgramSystemPath => _root;
    public string DataPath => _root;
    public string ImageCachePath => _root;
    public string PluginsPath => _root;
    public string PluginConfigurationsPath => _root;
    public string LogDirectoryPath => _root;
    public string ConfigurationDirectoryPath => _root;
    public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");
    public string CachePath { get => _root; set { } }
    public string TempDirectory => _root;
    public string VirtualDataPath => _root;
    public string TrickplayPath => _root;
    public string BackupPath => _root;
    public void MakeSanityCheckOrThrow() { }
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) { }
}

sealed class StubXml : MediaBrowser.Model.Serialization.IXmlSerializer
{
    public void SerializeToStream(object obj, Stream stream) { }
    public void SerializeToFile(object obj, string file) { }
    public object DeserializeFromFile(Type type, string file)
        => throw new FileNotFoundException("stub serializer", file);
    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException();
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException();
}
