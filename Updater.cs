using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace StutterFix
{
    // 자동 업데이트.
    // 게임을 켜고 조금 뒤(그 뒤로는 3시간마다) GitHub 의 최신 릴리스(api.github.com/repos/pding4569/StutterFix/releases/latest)를 확인한다.
    // 더 새 버전이 있으면 그 릴리스의 zip(플레이어용 또는 개발자용)을 알아서 받아 모드 폴더의 StutterFix.dll 과 Info.json 을 바꾼다.
    // 게임이 쓰는 DLL 은 UMM 이 복사본을 올려 두므로 바로 덮어쓸 수 있고, 새 버전은 게임을 다시 켜면 올라온다. 원래 DLL 은 StutterFix.dll.bak 으로 남긴다.
    // 곡에 영향이 없게: 확인·받기 시작과 결과 처리는 곡(에디터 재생 포함) 밖에서만 하고, 곡 중에 받기가 끝나면 곡이 끝날 때까지 미룬다.
    // 받는 것은 UnityWebRequest 가 자기 스레드에서, 압축 풀기와 파일 쓰기는 작업 스레드에서 한다(메인 스레드는 결과만 본다).
    // UMM 도 Info.json 의 Repository(repository.json)로 모드 목록에 새 버전을 표시한다. 릴리스할 때 repository.json 버전을 같이 올린다.
    internal static class Updater
    {
        private const string Api = "https://api.github.com/repos/pding4569/StutterFix/releases/latest";
        private const string DownloadPrefix = "https://github.com/pding4569/StutterFix/releases/download/";

        internal static string Latest = "";        // 최신 릴리스 버전 (예: 2.1.1)
        internal static string NotesUrl = "";      // 릴리스 페이지
        internal static string Status = "";        // 화면에 보일 상태
        internal static bool Available;             // 지금 버전보다 새 버전이 있음
        internal static bool Installed;             // 받아서 바꿔 둠 (다시 켜면 적용)
        internal static bool Busy;
        private static string zipUrl = "";
        private static string zipSha256 = "";
        private static volatile int generation;
        private static UnityWebRequest req;
        private static bool downloading;
        private static float startAt = -1f, nextCheck = -1f;
        private const float RecheckSeconds = 3 * 3600f;
        // 작업 스레드의 설치 결과 (메인 스레드가 Tick 에서 읽어 화면·로그에 반영)
        private static volatile int installState;   // 0 없음, 1 설치 중, 2 끝, 3 실패
        private static string installError = "";

        private static string Current { get { try { return Main.Entry.Info.Version; } catch { return "0"; } } }

        // OnUpdate 에서 부른다
        internal static void Tick()
        {
#if MELON
            return;   // MelonLoader 빌드: 받는 파일이 UMM 용 DLL 이라 확인·받기를 하지 않는다
#endif
            if (installState >= 2) InstallDone();
            if (Main.Config == null || Hitch.Playing) return;   // 곡 중에는 결과 처리도 새 요청도 하지 않는다
            if (req != null) { if (req.isDone) Finish(); return; }
            if (Busy || Installed || !Main.Config.CheckUpdates) return;
            float now = Time.realtimeSinceStartup;
            if (startAt < 0f) { startAt = now; nextCheck = now + 15f; }   // 켜진 직후에는 하지 않는다
            if (Available && zipUrl.Length > 0) { Download(); return; }   // 확인만 됐던 새 버전(자동 업데이트를 나중에 켠 경우)
            if (now < nextCheck) return;
            nextCheck = now + RecheckSeconds;
            Check();
        }

        internal static void Check()
        {
            if (req != null || Busy) return;
            try
            {
                req = UnityWebRequest.Get(Api);
                req.SetRequestHeader("User-Agent", "StutterFix/" + Current);
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 20;
                req.SendWebRequest();
                downloading = false; Busy = true;
                Status = SettingsWindow.T("새 버전 확인 중…", "Checking for updates…");
            }
            catch (Exception ex) { req = null; Busy = false; Status = SettingsWindow.T("확인 실패: ", "Check failed: ") + ex.Message; }
        }

        internal static void Download()
        {
            if (req != null || Busy || !Available || zipUrl.Length == 0) return;
            try
            {
                req = UnityWebRequest.Get(zipUrl);
                req.SetRequestHeader("User-Agent", "StutterFix/" + Current);
                req.timeout = 120;
                req.SendWebRequest();
                downloading = true; Busy = true;
                Status = SettingsWindow.T("받는 중…", "Downloading…");
            }
            catch (Exception ex) { req = null; Busy = false; Status = SettingsWindow.T("받기 실패: ", "Download failed: ") + ex.Message; }
        }

        private static void Finish()
        {
            var r = req; req = null; Busy = false;
            try
            {
                if (r.result != UnityWebRequest.Result.Success)
                {
                    Status = (downloading ? SettingsWindow.T("받기 실패: ", "Download failed: ") : SettingsWindow.T("확인 실패: ", "Check failed: ")) + r.error;
                    Main.Entry.Logger.Log("[업데이트] " + Status);
                    if (downloading) zipUrl = "";   // 같은 파일을 곧바로 다시 받지 않는다(다음 확인 때 다시)
                    return;
                }
                if (downloading)
                {
                    byte[] zip = r.downloadHandler.data;
                    string latest = Latest, dir = Main.Entry.Path, expectedSha = zipSha256;
                    int requestGeneration=generation;
                    Busy = true; installState = 1;
                    Status = SettingsWindow.T("설치하는 중…", "Installing…");
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { Install(zip, latest, dir,expectedSha,()=>generation!=requestGeneration); if(generation==requestGeneration)installState = 2; }
                        catch (Exception ex) { if(generation==requestGeneration){installError = ex.Message; installState = 3;} }
                    });
                }
                else
                {
                    Parse(r.downloadHandler.text);
                    if (Available && zipUrl.Length > 0 && Main.Config.CheckUpdates) Download();   // 새 버전이면 바로 받는다
                }
            }
            catch (Exception ex) { Status = SettingsWindow.T("실패: ", "Failed: ") + ex.Message; Main.Entry.Logger.Log("[업데이트] 실패: " + ex); }
            finally { r.Dispose(); }
        }

        // 작업 스레드의 설치가 끝났을 때 (메인 스레드)
        private static void InstallDone()
        {
            int s = installState; installState = 0; Busy = false;
            if (s == 2)
            {
                Installed = true; Available = false; noticeUntil = -1f;
                Status = string.Format(SettingsWindow.T("v{0} 로 자동 업데이트했습니다. 게임을 다시 켜면 적용됩니다", "Updated to v{0}. Restart the game to apply"), Latest);
                Main.Entry.Logger.Log("[업데이트] v" + Latest + " 설치 (다시 켜면 적용)");
            }
            else
            {
                zipUrl = "";
                Status = SettingsWindow.T("설치 실패: ", "Install failed: ") + installError;
                Main.Entry.Logger.Log("[업데이트] 설치 실패: " + installError);
            }
        }

        private static void Parse(string json)
        {
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
            if (!tag.Success) { Status = SettingsWindow.T("릴리스 정보를 읽지 못했습니다", "Could not read release info"); return; }
            Latest = tag.Groups[1].Value;
            var page = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github.com/pding4569/StutterFix/releases/[^\"]+)\"");
            NotesUrl = page.Success ? page.Groups[1].Value : "https://github.com/pding4569/StutterFix/releases";
            string want = "StutterFix-" + Latest + "-" + (Edition.Dev ? "developer" : "player") + ".zip";
            zipUrl = "";
            zipSha256="";
            if(ReleaseAssetDigest.TryFind(json,want,DownloadPrefix,out var assetUrl,out var assetSha)){zipUrl=assetUrl;zipSha256=assetSha;}
            Notes = CleanNotes(JsonString(json, "body"));
            Available = Newer(Latest, Current);
            Status = Available
                ? string.Format(SettingsWindow.T("새 버전 v{0} 이 있습니다 (지금 v{1})", "New version v{0} available (you have v{1})"), Latest, Current)
                : string.Format(SettingsWindow.T("최신 버전입니다 (v{0})", "Up to date (v{0})"), Current);
            if (Available && zipUrl.Length == 0) Status += SettingsWindow.T(" - 검증 가능한 파일이 없어 릴리스 페이지에서 직접 받아야 합니다", " - no verifiable download file; get it from the release page");
            Main.Entry.Logger.Log("[업데이트] 최신 v" + Latest + ", 지금 v" + Current + (Available ? " -> 새 버전 있음" : ""));
        }

        internal static string Notes = "";   // 패치노트 (릴리스 본문, 마크다운 기호를 걷어낸 것)

        // JSON 안의 문자열 값 하나 (이스케이프 풀기)
        private static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"");
            if (!m.Success) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = m.Index + m.Length; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"') break;
                if (ch != '\\' || i + 1 >= json.Length) { sb.Append(ch); continue; }
                char e = json[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': break;
                    case 't': sb.Append("  "); break;
                    case 'u':
                        if (i + 4 < json.Length) { try { sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16)); } catch { } i += 4; }
                        break;
                    default: sb.Append(e); break;   // \" \\ \/
                }
            }
            return sb.ToString();
        }

        // 마크다운을 창에 보이기 좋게: 제목 #, 굵게 **, 코드 `, 링크 [글](주소) -> 글, 목록 - -> ·
        private static string CleanNotes(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            var lines = md.Replace("\r", "").Split('\n');
            var sb = new System.Text.StringBuilder();
            foreach (var raw in lines)
            {
                string l = raw.TrimEnd();
                l = Regex.Replace(l, "^#+\\s*", "");
                l = Regex.Replace(l, "^(\\s*)[-*]\\s+", "$1· ");
                l = Regex.Replace(l, "\\[([^\\]]+)\\]\\([^)]+\\)", "$1");
                l = l.Replace("**", "").Replace("`", "");
                if (l.Length == 0 && (sb.Length == 0 || sb.ToString().EndsWith("\n\n"))) continue;   // 빈 줄은 하나만
                sb.Append(l).Append('\n');
            }
            string s = sb.ToString().Trim();
            if (s.Length > 2500) s = s.Substring(0, 2500) + "\n…";
            return s;
        }

        private static bool Newer(string a, string b)
        {
            try { return new Version(Pad(a)) > new Version(Pad(b)); } catch { return false; }
        }
        private static string Pad(string v) { var p = v.Split('.'); return p.Length >= 2 ? v : v + ".0"; }

        // 작업 스레드에서 돈다: 유니티 API·화면 상태를 건드리지 않는다
        private static void Install(byte[] zip, string latest, string dir,string expectedSha,Func<bool> cancelled)
        {
            if(cancelled())throw new OperationCanceledException();
            UpdateTransaction.VerifyPackage(zip,expectedSha);
            byte[] dll = null; string info = null;
            using (var ms = new MemoryStream(zip))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in za.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (n == "StutterFix/StutterFix.dll")
                    {
                        if(dll!=null||e.Length>32*1024*1024)throw new InvalidDataException("Duplicate/oversized DLL");
                        dll = ReadAll(e);
                    }
                    else if (n == "StutterFix/Info.json")
                    {
                        if(info!=null||e.Length>65536)throw new InvalidDataException("Duplicate/oversized Info.json");
                        info = System.Text.Encoding.UTF8.GetString(ReadAll(e));
                    }
                }
            }
            // 받은 것이 맞는지: DLL 모양(MZ), Info.json 의 Id 와 버전
            if (dll == null || dll.Length < 1024 || dll[0] != (byte)'M' || dll[1] != (byte)'Z') throw new Exception("zip 안에 올바른 StutterFix.dll 이 없음");
            if (info == null || !Regex.IsMatch(info, "\"Id\"\\s*:\\s*\"StutterFix\"") || !Regex.IsMatch(info, "\"Version\"\\s*:\\s*\"" + Regex.Escape(latest) + "\""))
                throw new Exception("zip 안의 Info.json 이 맞지 않음");
            UpdateTransaction.Apply(dir,dll,new System.Text.UTF8Encoding(false,true).GetBytes(info),validateAssembly:path=>
            {
                if(System.Reflection.AssemblyName.GetAssemblyName(path).Name!="StutterFix")throw new BadImageFormatException("Unexpected assembly name");
            },cancelled:cancelled);
        }

        internal static void Shutdown()
        {
            generation++;
            try{if(req!=null){req.Abort();req.Dispose();}}catch{}
            req=null;Busy=false;installState=0;startAt=nextCheck=-1f;
        }
#if DEV || AUTOTEST
        internal static void TestInstall(byte[] bytes,string directory,string digest){Install(bytes,"9.9.9",directory,digest,()=>false);}
#endif

        private static byte[] ReadAll(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var o = new MemoryStream())
            {
                s.CopyTo(o);
                return o.ToArray();
            }
        }

        // 안내: 자동 업데이트를 마쳤거나(다시 켜면 적용), 자동 업데이트를 끈 채 새 버전이 있으면
        // 곡 밖에서 20초 동안 오른쪽 위에 작게 (설정 창이 닫혀 있을 때)
        private static float noticeUntil = -1f;
        private static GUIStyle noticeStyle;
        internal static void DrawNotice(bool windowOpen)
        {
            bool manual = Available && !Main.Config.CheckUpdates;
            if (!(Installed || manual) || windowOpen || Hitch.Playing || Event.current.type != EventType.Repaint) return;
            if (noticeUntil < 0f) noticeUntil = Time.realtimeSinceStartup + 20f;
            if (Time.realtimeSinceStartup > noticeUntil) return;
            if (noticeStyle == null)
            {
                noticeStyle = new GUIStyle(GUI.skin.label) { font = SettingsWindow.UiFont(), fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = false };
                noticeStyle.normal.textColor = Color.white;
            }
            string key = Hotkey.Name(Main.Config.WindowKey, Main.Config.WindowMods);
            var text = new GUIContent(Installed
                ? string.Format(SettingsWindow.T("Stutter Fix v{0} 로 자동 업데이트함 · 게임을 다시 켜면 적용", "Stutter Fix updated to v{0} · restart the game to apply"), Latest)
                : string.Format(SettingsWindow.T("Stutter Fix 새 버전 v{0} · {1} 키 → 홈에서 업데이트", "Stutter Fix v{0} available · press {1} → Home to update"), Latest, key));
            var size = noticeStyle.CalcSize(text);
            var r = new Rect(Screen.width - size.x - 44, 20, size.x + 28, 40);
            var old = GUI.color;
            GUI.color = new Color(0.06f, 0.065f, 0.08f, 0.88f);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, GUI.color, 0, 10);
            GUI.color = old;
            GUI.Label(r, text, noticeStyle);
        }
    }
}
