using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VInc.AvatarHub
{
    /// <summary>アップロードのキューの 1 行（アバター 1 体）。</summary>
    [Serializable]
    public class UploadJob
    {
        public string id;
        /// <summary>シーンでの名前（追加したときのもの）。</summary>
        public string name = "";
        /// <summary>アバターがあるシーン（保存していないシーンなら空）。</summary>
        public string scenePath = "";
        /// <summary>Hierarchy での場所（例：Avatars/Rusk）。ID で見つからないときに使う。</summary>
        public string objectPath = "";
        /// <summary>VRChat のアバター ID（avtr_…）。Pipeline Manager の Blueprint ID。</summary>
        public string blueprintId = "";
        /// <summary>VRChat 上の名前（準備のときに取ってくる）。</summary>
        public string remoteName = "";
        /// <summary>waiting / preparing / agreeing / ready / building / uploading / done / failed / skipped / cancelled / interrupted</summary>
        public string status = "waiting";
        public string message = "";
        /// <summary>いまの段階（ビルド中・アップロード中など）。</summary>
        public string phase = "";
        /// <summary>いまの段階の進み具合（0〜1。わからないときは -1）。</summary>
        public float progress = -1;
        /// <summary>ビルドとアップロードにかかった秒数。</summary>
        public double seconds;
        public string finishedAt = "";
        /// <summary>アップロードの間だけ表示にしているもの（Hierarchy の場所）。途中で再読み込みされたときに戻すため。</summary>
        public List<string> shown = new List<string>();
        /// <summary>アップロードしようとした回数。Undo で、実行した結果を古い状態に戻さないため。</summary>
        public int runs;

        public string DisplayName => string.IsNullOrEmpty(remoteName) || remoteName == name ? name : $"{name}（VRChat：{remoteName}）";
        public string SceneName => string.IsNullOrEmpty(scenePath) ? "保存していないシーン" : Path.GetFileNameWithoutExtension(scenePath);
        public bool Finished => status == "done" || status == "failed" || status == "skipped";
        public bool Busy => status == "preparing" || status == "agreeing" || status == "building" || status == "uploading";
        /// <summary>「開始」で（もう一度）アップロードするもの。</summary>
        public bool Runnable => status == "waiting" || status == "ready" || status == "cancelled" || status == "interrupted";
    }

    [Serializable]
    public class UploadSession
    {
        public string kind = "AvatarHub.UploadQueue";
        public List<UploadJob> jobs = new List<UploadJob>();
        public bool running;
        /// <summary>始めた時刻（EditorApplication.timeSinceStartup）。効果音を鳴らすか決めるため。</summary>
        public double startedAt;
        /// <summary>失敗したら止める。false なら飛ばして次へ進む。</summary>
        public bool stopOnError;
        /// <summary>いまのアバターが終わったら止める。</summary>
        public bool stopAfterCurrent;
        /// <summary>止まった理由など（画面に出す）。</summary>
        public string notice = "";
        /// <summary>アップロードのために追加で開いているシーン（無ければ空）。</summary>
        public string openedScene = "";
        public bool openedSceneListed;

        public int DoneCount => jobs.Count(j => j.Finished);
        public bool Complete => jobs.Count > 0 && jobs.All(j => j.Finished);
        public UploadJob Find(string id) => jobs.Find(j => j.id == id);
    }

    /// <summary>
    /// アップロードの実際の処理（VRChat SDK を呼ぶ部分）。SDK が入っているときだけ、別のアセンブリ（VInc.AvatarHub.VRChat）が登録する。
    /// テストでは偽物に差し替える。
    /// </summary>
    public interface IAvatarUploadBackend
    {
        /// <summary>VRChat SDK（Avatars）のバージョン。</summary>
        string SdkVersion { get; }
        /// <summary>root とその子にあるアバター（VRC Avatar Descriptor が付いたもの）。</summary>
        List<GameObject> FindAvatars(GameObject root);
        /// <summary>Pipeline Manager の Blueprint ID（無ければ空）。</summary>
        string BlueprintId(GameObject avatar);
        bool PanelOpen { get; }
        /// <summary>SDK の画面を開く（開いていれば前に出す）。ログインしていれば Builder タブにする。</summary>
        void OpenPanel();
        bool LoggedIn { get; }
        string UserName { get; }
        /// <summary>いまのビルド先（Windows / Android など）。</summary>
        string Platform { get; }
        /// <summary>VRChat 上のアバターの情報を取ってくる（持ち主かどうかも確かめる）。VRChat 上の名前を返す。</summary>
        Task<string> Prepare(string blueprintId, CancellationToken ct);
        /// <summary>著作権の同意。この Unity のセッションでまだなら、SDK の画面に確認が出る。</summary>
        Task Agree(string blueprintId, CancellationToken ct);
        /// <summary>ビルドしてアップロードする。report(段階, 進み具合 0〜1 / -1, アップロード中か)</summary>
        Task Upload(GameObject avatar, Action<string, float, bool> report, CancellationToken ct);
        void CancelUpload();
    }

    public class AvatarUploadException : Exception
    {
        /// <summary>"declined"（同意しなかった）/ "unchanged"（中身が同じなのでアップロードを省いた）/ ""（失敗）</summary>
        public readonly string kind;

        public AvatarUploadException(string message, string kind = "", Exception inner = null) : base(message, inner)
        {
            this.kind = kind ?? "";
        }
    }

    /// <summary>
    /// ⑦ アバターアップローダー：キューのアバターを順番にビルドして VRChat へアップロードする。
    /// 最初に全員分の準備と著作権の同意（SDK の画面に出る。ここだけ人の操作が要る）を済ませ、そのあとは止まらずに続ける。
    /// アップロードは VRChat SDK を通す（SDK の画面を開いたまま動く）。キューは Library/VIAH に保存する。
    /// </summary>
    [InitializeOnLoad]
    public static class AvatarUploader
    {
        public const string SoundEvent = "upload";

        /// <summary>SDK と話す部分。SDK が入っていなければ null。</summary>
        public static IAvatarUploadBackend Backend { get; set; }

        /// <summary>キューの状態が変わったとき（画面の描き直し用）。</summary>
        public static event Action Changed;

        /// <summary>開発用：キューの保存先を差し替える（テストやガイド画像の撮影で、実際のキューを汚さないように）。</summary>
        public static string DirOverride;

        /// <summary>開発用：SDK の画面とログインを待つ秒数。</summary>
        public static double WaitSeconds = 20;

        static UploadSession session;
        static CancellationTokenSource cts;
        static double lastPing;

        /// <summary>このドメインで実行中か（リロードすると false に戻る）。</summary>
        public static bool Working { get; private set; }

        /// <summary>実行中の処理（テストで終わりを待つため）。</summary>
        public static Task Current { get; private set; } = Task.CompletedTask;

        static AvatarUploader()
        {
            EditorApplication.delayCall += RecoverAfterReload;
        }

        static string FilePath => Path.Combine(DirOverride ?? Path.Combine(ViahStorage.ProjectRoot, "Library", "VIAH"), "upload-queue.json");

        public static UploadSession Session
        {
            get
            {
                if (session != null) return session;
                var path = FilePath;
                session = File.Exists(path) ? ViahStorage.Read<UploadSession>(path) : null;
                session ??= new UploadSession();
                session.jobs ??= new List<UploadJob>();
                session.notice ??= "";
                session.openedScene ??= "";
                foreach (var j in session.jobs) j.shown ??= new List<string>();
                return session;
            }
        }

        /// <summary>読み込み済みのキューを捨てる（次に使うときにファイルから読み直す）。</summary>
        public static void ResetCache() => session = null;

        public static void Save()
        {
            try { ViahStorage.Write(FilePath, Session); }
            catch (Exception e) { Debug.LogWarning("[Avatar Hub] アップロードのキューを保存できませんでした: " + e.Message); }
            Changed?.Invoke();
        }

        /// <summary>進み具合の通知（多いので 0.2 秒に 1 回まで。保存はしない）。</summary>
        static void Ping()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - lastPing < 0.2) return;
            lastPing = now;
            Changed?.Invoke();
        }

        /// <summary>リロード（コンパイルなど）で実行中の処理が消えたら、途中だった行を「中断」にして止める。</summary>
        static void RecoverAfterReload()
        {
            if (Working) return;
            UploadSession s;
            try { s = Session; }
            catch { return; }
            if (!s.running) return;
            foreach (var j in s.jobs.Where(x => x.Busy || x.status == "ready"))
            {
                bool wasUploading = j.status == "building" || j.status == "uploading";
                j.status = wasUploading ? "interrupted" : "waiting";
                j.message = wasUploading ? "Unity の再読み込み（コンパイルなど）で中断されました。VRChat に上がったかは SDK の画面（Content Manager）で確認してください。" : "";
                j.phase = "";
                j.progress = -1;
                // アップロードの間だけ表示にしていたものを戻す
                var go = j.shown.Count > 0 ? Find(j) : null;
                for (var t = go != null ? go.transform : null; t != null; t = t.parent)
                    if (j.shown.Contains(PathOf(t))) t.gameObject.SetActive(false);
                j.shown.Clear();
            }
            CloseOpenedScene(s);
            s.running = false;
            s.notice = "Unity の再読み込みで止まりました。「続きを開始」でもう一度始められます。";
            Save();
            ViahSound.Attention(SoundEvent, Elapsed(s));
        }

        static double Elapsed(UploadSession s) => s.startedAt > 0 ? EditorApplication.timeSinceStartup - s.startedAt : 0;

        // ───────────── キューの編集 ─────────────

        public static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }

        /// <summary>開いているシーンのアバターをすべて。</summary>
        public static List<GameObject> LoadedAvatars()
        {
            var list = new List<GameObject>();
            if (Backend == null) return list;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) list.AddRange(Backend.FindAvatars(root));
            }
            return list;
        }

        /// <summary>キューに追加する（root の子にあるアバターも）。追加できなかった理由を problems に入れて、追加した数を返す。</summary>
        public static int Add(IEnumerable<GameObject> objects, List<string> problems)
        {
            if (Backend == null)
            {
                problems.Add("VRChat SDK（Avatars）3.10.6 以降が入っていません");
                return 0;
            }
            var s = Session;
            int added = 0;
            foreach (var go in objects.Where(o => o != null).Distinct())
            {
                if (EditorUtility.IsPersistent(go))
                {
                    problems.Add($"「{go.name}」はプレハブ（ファイル）です。シーンに置いたアバターを Hierarchy から入れてください");
                    continue;
                }
                var avatars = Backend.FindAvatars(go);
                if (avatars.Count == 0)
                {
                    problems.Add($"「{go.name}」はアバターではありません（VRC Avatar Descriptor が付いていません）");
                    continue;
                }
                foreach (var av in avatars)
                {
                    var id = Backend.BlueprintId(av) ?? "";
                    if (string.IsNullOrEmpty(id))
                    {
                        problems.Add($"「{av.name}」はまだ一度もアップロードされていません。最初の 1 回は SDK の画面でアップロードしてください（名前やサムネイルを決めるため）");
                        continue;
                    }
                    var same = s.jobs.Find(j => j.blueprintId == id);
                    if (same != null && !same.Finished)
                    {
                        problems.Add($"「{av.name}」はすでにキューにあります（同じアバター ID）");
                        continue;
                    }
                    if (same != null) s.jobs.Remove(same);
                    s.jobs.Add(new UploadJob
                    {
                        id = ViahStorage.NewId(),
                        name = av.name,
                        scenePath = av.scene.path ?? "",
                        objectPath = PathOf(av.transform),
                        blueprintId = id,
                    });
                    added++;
                }
            }
            Save();
            return added;
        }

        /// <summary>キューの行のアバターを、開いているシーンから探す。</summary>
        public static GameObject Find(UploadJob j) => Find(j, out _);

        /// <summary>
        /// キューの行のアバターを探す。同じ ID のコピーが別のシーンにあっても取り違えないよう、登録したシーンの中だけを見る。
        /// 同じシーンに同じ ID が複数あるときは Hierarchy の場所で決め、決められなければ error に理由を入れて null を返す。
        /// </summary>
        static GameObject Find(UploadJob j, out string error)
        {
            error = null;
            if (Backend == null) return null;
            var hits = LoadedAvatars().Where(g => Backend.BlueprintId(g) == j.blueprintId).ToList();
            if (!string.IsNullOrEmpty(j.scenePath)) hits = hits.Where(g => g.scene.path == j.scenePath).ToList();
            if (hits.Count <= 1) return hits.FirstOrDefault();
            var atPath = hits.Where(g => PathOf(g.transform) == j.objectPath).ToList();
            if (atPath.Count == 1) return atPath[0];
            error = $"同じアバター ID のアバターが {hits.Count} 体あり、どれをアップロードするか決められません";
            return null;
        }

        // ───────────── 実行 ─────────────

        /// <summary>始められない理由（始められるなら null）。</summary>
        public static string CannotStart()
        {
            if (Backend == null) return "VRChat SDK（Avatars）3.10.6 以降が入っていません。VCC で入れるか、更新してください。";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "再生中はアップロードできません。再生を止めてください。";
            if (EditorApplication.isCompiling) return "スクリプトのコンパイル中です。終わるまで待ってください。";
            if (Session.running || Working) return "アップロード中です。";
            if (PackageImportRunner.Active != null) return "④ インポートの実行中です。終わってから始めてください。";
            if (!Session.jobs.Any(j => j.Runnable)) return "アップロードを待っているアバターがありません。";
            return null;
        }

        /// <summary>始める。準備（同意）→ 順番にビルドとアップロード。終わるまで待つ Task を返す（画面からは待たなくてよい）。</summary>
        public static Task Start()
        {
            if (CannotStart() != null) return Task.CompletedTask;
            var s = Session;
            foreach (var j in s.jobs.Where(x => x.Runnable))
            {
                j.status = "waiting";
                j.message = "";
                j.phase = "";
                j.progress = -1;
                j.runs++;
            }
            s.running = true;
            s.stopAfterCurrent = false;
            s.notice = "";
            s.startedAt = EditorApplication.timeSinceStartup;
            Save();
            cts = new CancellationTokenSource();
            Current = RunSafe(s, cts.Token);
            return Current;
        }

        /// <summary>いまのアバターが終わったところで止める。</summary>
        public static void StopAfterCurrent()
        {
            if (!Session.running) return;
            Session.stopAfterCurrent = true;
            Save();
        }

        /// <summary>今すぐ止める（アップロード中のものは取り消す。ビルド中のものはビルドが終わってから止まる）。</summary>
        public static void Abort()
        {
            if (!Session.running) return;
            Session.stopAfterCurrent = true;
            Save();
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { }
            try { Backend?.CancelUpload(); }
            catch (Exception e) { Debug.LogWarning("[Avatar Hub] アップロードを取り消せませんでした: " + e.Message); }
        }

        static async Task RunSafe(UploadSession s, CancellationToken ct)
        {
            Working = true;
            int failed = 0;
            bool halted = false;
            try
            {
                (failed, halted) = await Run(s, ct);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                s.notice = "思わぬエラーで止まりました：" + e.Message;
                halted = true;
            }
            finally
            {
                Working = false;
                foreach (var j in s.jobs.Where(x => x.Busy || x.status == "ready"))
                {
                    j.status = j.status == "building" || j.status == "uploading" ? "cancelled" : "waiting";
                    j.phase = "";
                    j.progress = -1;
                }
                CloseOpenedScene(s);
                bool userStopped = s.stopAfterCurrent;
                s.running = false;
                s.stopAfterCurrent = false;
                Save();
                cts?.Dispose();
                cts = null;
                if (halted) ViahSound.Attention(SoundEvent, Elapsed(s));
                else if (!userStopped) PlayFinished(failed, Elapsed(s));
            }
        }

        /// <summary>全部終わった：失敗が無ければ「終わった」の音、あれば「確認して」の音。</summary>
        static void PlayFinished(int failed, double seconds)
        {
            if (failed > 0) ViahSound.Attention(SoundEvent, seconds);
            else ViahSound.Done(SoundEvent, seconds);
        }

        /// <summary>返り値：(失敗した数, 確認が要る理由で止まったか)</summary>
        static async Task<(int failed, bool halted)> Run(UploadSession s, CancellationToken ct)
        {
            var b = Backend;
            int failed = 0;
            // 始めるときは、すでに開いていても SDK の画面を前に出す（同意の確認が隠れて止まって見えないように）
            if (!await EnsurePanel(s, b, ct, true)) return (0, !ct.IsCancellationRequested);

            // 1. 準備と著作権の同意（ここだけ人の操作が要る。終われば、あとは止まらずに続く）
            foreach (var j in s.jobs.Where(x => x.status == "waiting").ToList())
            {
                if (ct.IsCancellationRequested || s.stopAfterCurrent) return (failed, false);
                if (!s.jobs.Contains(j)) continue;
                await Prepare(s, b, j, ct);
                if (j.status == "failed")
                {
                    failed++;
                    if (s.stopOnError) return (failed, Halt(s, $"「{j.name}」の準備に失敗したので止めました。"));
                }
            }

            // 2. 順番にビルドしてアップロード
            foreach (var j in s.jobs.Where(x => x.status == "ready").ToList())
            {
                if (ct.IsCancellationRequested || s.stopAfterCurrent) return (failed, false);
                if (!s.jobs.Contains(j)) continue;
                if (!await EnsurePanel(s, b, ct, false)) return (failed, !ct.IsCancellationRequested);
                await UploadOne(s, b, j, ct);
                if (j.status == "cancelled") return (failed, false);
                if (j.status == "failed")
                {
                    failed++;
                    if (s.stopOnError) return (failed, Halt(s, $"「{j.name}」のアップロードに失敗したので止めました。"));
                }
            }
            return (failed, false);
        }

        static bool Halt(UploadSession s, string notice)
        {
            s.notice = notice;
            return true;
        }

        /// <summary>SDK の画面を開いて、ログインを待つ。show なら、すでに開いていても前に出す。</summary>
        static async Task<bool> EnsurePanel(UploadSession s, IAvatarUploadBackend b, CancellationToken ct, bool show)
        {
            if (!show && b.PanelOpen && b.LoggedIn) return true;
            b.OpenPanel();
            if (b.PanelOpen && b.LoggedIn) return true;
            var until = EditorApplication.timeSinceStartup + WaitSeconds;
            while (!(b.PanelOpen && b.LoggedIn))
            {
                if (ct.IsCancellationRequested) return false;
                if (EditorApplication.timeSinceStartup > until)
                {
                    s.notice = !b.PanelOpen ? "VRChat SDK の画面を開けませんでした。メニューの「VRChat SDK > Show Control Panel」で開いてから、もう一度始めてください。"
                        : "VRChat SDK の画面でログインしてから、もう一度「開始」を押してください。";
                    return false;
                }
                await Task.Delay(250);
            }
            b.OpenPanel(); // ログインできたら Builder タブにする
            return true;
        }

        static async Task Prepare(UploadSession s, IAvatarUploadBackend b, UploadJob j, CancellationToken ct)
        {
            try
            {
                j.status = "preparing";
                j.phase = "VRChat からアバターの情報を取得中";
                Save();
                var remote = await b.Prepare(j.blueprintId, ct);
                if (!string.IsNullOrEmpty(remote)) j.remoteName = remote;
                ct.ThrowIfCancellationRequested();
                j.status = "agreeing";
                j.phase = "著作権の同意を待っています（SDK の画面）";
                Save();
                await b.Agree(j.blueprintId, ct);
                j.status = "ready";
                j.phase = "";
                j.message = "";
            }
            catch (OperationCanceledException)
            {
                j.status = "waiting";
                j.phase = "";
            }
            catch (AvatarUploadException e) when (e.kind == "declined")
            {
                j.status = "skipped";
                j.phase = "";
                j.message = e.Message;
            }
            catch (Exception e)
            {
                Fail(j, MessageOf(e));
            }
            Save();
        }

        static async Task UploadOne(UploadSession s, IAvatarUploadBackend b, UploadJob j, CancellationToken ct)
        {
            var go = Find(j, out var error);
            if (go == null && error == null && OpenSceneOf(s, j, out error)) go = Find(j, out error);
            if (go == null)
            {
                Fail(j, error ?? "アバターが見つかりません（シーンが閉じられたか、名前・ID が変わった可能性があります）");
                Save();
                return;
            }

            var restore = ShowWhileUploading(j, go, out bool wasHidden);
            var started = EditorApplication.timeSinceStartup;
            j.status = "building";
            j.phase = "ビルドの準備中";
            j.progress = -1;
            j.message = wasHidden ? "非表示だったので、アップロードの間だけ表示にしました" : "";
            Save();
            try
            {
                await b.Upload(go, (phase, p, uploading) =>
                {
                    var st = uploading ? "uploading" : "building";
                    bool changed = st != j.status;
                    j.status = st;
                    j.phase = phase ?? "";
                    j.progress = p;
                    if (changed) Save();
                    else Ping();
                }, ct);
                j.status = "done";
                j.progress = 1;
            }
            catch (OperationCanceledException)
            {
                j.status = "cancelled";
                j.message = "中止しました";
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // 取り消しを、SDK が別の例外（Request Cancelled など）で知らせることがある
                j.status = "cancelled";
                j.message = "中止しました";
            }
            catch (AvatarUploadException e) when (e.kind == "unchanged")
            {
                j.status = "done";
                j.message = e.Message;
            }
            catch (Exception e)
            {
                Fail(j, MessageOf(e));
            }
            finally
            {
                restore();
                j.phase = "";
                j.seconds = EditorApplication.timeSinceStartup - started;
                j.finishedAt = DateTime.Now.ToString("HH:mm");
                Save();
            }
        }

        /// <summary>
        /// 非表示のアバター（親が非表示のときも）は、アップロードの間だけ表示にする。戻す処理を返す。
        /// SDK がその間にシーンを保存したときは、表示にしたままの状態がファイルに残らないよう、戻してから保存し直す。
        /// </summary>
        static Action ShowWhileUploading(UploadJob j, GameObject go, out bool wasHidden)
        {
            var changed = new List<GameObject>();
            j.shown.Clear();
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (t.gameObject.activeSelf) continue;
                t.gameObject.SetActive(true);
                changed.Add(t.gameObject);
                j.shown.Add(PathOf(t));
            }
            wasHidden = changed.Count > 0;
            var scene = go.scene;
            var stamp = FileStamp(scene.path);
            return () =>
            {
                foreach (var g in changed)
                    if (g != null) g.SetActive(false);
                j.shown.Clear();
                if (changed.Count > 0 && scene.IsValid() && scene.isLoaded && stamp != FileStamp(scene.path)) EditorSceneManager.SaveScene(scene);
            };
        }

        /// <summary>シーンのファイルが書き換わったかを見るための目印。</summary>
        static string FileStamp(string path) =>
            string.IsNullOrEmpty(path) || !File.Exists(path) ? "" : new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks;

        /// <summary>
        /// アバターのシーンが開いていなければ、今のシーンに追加で開く。
        /// 開いているシーンは閉じないので、保存していない変更を保存することも、捨てることもない。
        /// </summary>
        static bool OpenSceneOf(UploadSession s, UploadJob j, out string error)
        {
            error = null;
            if (!SceneExists(j.scenePath))
            {
                error = "アバターが見つかりません（シーンが閉じられたか、削除された可能性があります）";
                return false;
            }
            var listed = SceneManager.GetSceneByPath(j.scenePath);
            if (listed.IsValid() && listed.isLoaded)
            {
                error = $"シーン「{j.SceneName}」の中にアバターが見つかりません（名前や ID が変わった可能性があります）";
                return false;
            }
            try
            {
                CloseOpenedScene(s);
                // Hierarchy に「読み込んでいないシーン」として並んでいたものは、終わっても並びから外さない
                s.openedSceneListed = listed.IsValid();
                EditorSceneManager.OpenScene(j.scenePath, OpenSceneMode.Additive);
                s.openedScene = j.scenePath;
                Save();
                return true;
            }
            catch (Exception e)
            {
                error = $"シーン「{j.SceneName}」を開けませんでした：{e.Message}";
                return false;
            }
        }

        /// <summary>アップロードのために追加で開いたシーンを、保存せずに閉じる（自分で開いたものだけ）。</summary>
        static void CloseOpenedScene(UploadSession s)
        {
            if (string.IsNullOrEmpty(s.openedScene)) return;
            var scene = SceneManager.GetSceneByPath(s.openedScene);
            s.openedScene = "";
            try
            {
                if (scene.IsValid() && scene.isLoaded && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(scene, !s.openedSceneListed);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Avatar Hub] アップロードのために開いたシーンを閉じられませんでした: " + e.Message);
            }
        }

        static bool SceneExists(string path) => !string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null;

        static void Fail(UploadJob j, string message)
        {
            j.status = "failed";
            j.phase = "";
            j.progress = -1;
            j.message = message;
        }

        static string MessageOf(Exception e)
        {
            if (e is AggregateException a && a.InnerExceptions.Count == 1) e = a.InnerExceptions[0];
            return e is AvatarUploadException ? e.Message : $"{e.GetType().Name}：{e.Message}";
        }
    }
}
