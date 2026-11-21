using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace VInc.AvatarHub.UI
{
    /// <summary>
    /// ⑦ アバターアップローダー：キューに並べたアバターを、上から順番に VRChat へアップロードする。
    /// 最初に全員分の著作権の同意（SDK の画面）を済ませ、そのあとはビルドとアップロードを自動で続ける。終わると音で知らせる。
    /// </summary>
    public class UploaderPage : Page
    {
        public override string Section => "uploader";

        static UploadSession S => AvatarUploader.Session;
        static IAvatarUploadBackend B => AvatarUploader.Backend;

        VisualElement sdkBox;
        VisualElement queueBox;
        VisualElement progressBox;
        VisualElement resultBox;
        VisualElement footer;
        Segmented policy;

        // ───────────── Undo（キューの編集） ─────────────

        [Serializable]
        class UiState
        {
            public List<UploadJob> jobs = new List<UploadJob>();
            public bool stopOnError;
        }

        public override string CaptureState()
        {
            var s = new UiState { stopOnError = S.stopOnError };
            foreach (var j in S.jobs) s.jobs.Add(new UploadJob { id = j.id, name = j.name, scenePath = j.scenePath, objectPath = j.objectPath, blueprintId = j.blueprintId, remoteName = j.remoteName });
            return JsonUtility.ToJson(s);
        }

        public override void RestoreState(string state)
        {
            // アップロード中は並びを変えない（進み具合と合わなくなるため）
            if (S.running) return;
            var s = JsonUtility.FromJson<UiState>(state);
            var old = S.jobs.ToDictionary(j => j.id);
            S.jobs = s.jobs.Select(j => old.TryGetValue(j.id, out var o) ? o : j).ToList();
            S.stopOnError = s.stopOnError;
            AvatarUploader.Save();
            policy?.Set(S.stopOnError ? 1 : 0);
        }

        // ───────────── 画面 ─────────────

        public override void Build()
        {
            var page = UIKit.Div();
            page.style.flexGrow = 1;
            root.Add(page);
            var scroll = UIKit.PageScroll();
            page.Add(scroll);
            var c = scroll.contentContainer;

            c.Add(UIKit.Text("アバターアップローダー", "viah-page-title"));
            c.Add(UIKit.Text("複数のアバターを、上から順番に続けて VRChat へアップロードします。", "viah-page-sub"));
            c.Add(UIKit.Hint("upload.intro", "どう動くか",
                "「アップロード開始」を押すと VRChat SDK の画面が開きます。最初に、アバターごとに SDK の画面で著作権の同意（OK）を押してください（Unity を起動してから初めてのアバターだけ出ます）。" +
                "全部押し終われば、あとはビルドとアップロードを上から順番に自動で続けます。全部終わると音で知らせます。"));

            var sdkCard = UIKit.Card();
            sdkCard.name = "card-upload-sdk";
            sdkCard.Add(UIKit.SectionHead("1", "VRChat SDK", "アップロードは VRChat SDK を通して行います（SDK の画面を開いたまま動きます）"));
            sdkBox = UIKit.Div();
            sdkCard.Add(sdkBox);
            c.Add(sdkCard);

            if (B == null)
            {
                RenderSdk();
                return;
            }

            var addCard = UIKit.Card();
            addCard.name = "card-upload-add";
            addCard.Add(UIKit.SectionHead("2", "アップロードするアバターを追加", "Hierarchy からドロップします。親をドロップすると、中にあるアバターを全部入れます"));
            addCard.Add(AvatarDropZone());
            var addRow = UIKit.Row("viah-mt8", "viah-btn-group");
            addRow.Add(UIKit.Btn("Toolbar Plus", "Hierarchy で選択中のアバターを追加", () =>
            {
                var sel = Selection.gameObjects.Where(g => !EditorUtility.IsPersistent(g)).ToList();
                if (sel.Count == 0)
                {
                    window.Toast("Hierarchy でアバターを選んでから押してください", "warn");
                    return;
                }
                Add(sel);
            }, "small"));
            addRow.Add(UIKit.Btn("開いているシーンのアバターを全部追加", () =>
            {
                var all = AvatarUploader.LoadedAvatars();
                if (all.Count == 0)
                {
                    window.Toast("開いているシーンにアバターがありません", "warn");
                    return;
                }
                Add(all);
            }, "small", "viah-ml4"));
            addCard.Add(addRow);
            c.Add(addCard);

            var optCard = UIKit.Card();
            optCard.name = "card-upload-policy";
            optCard.Add(UIKit.SectionHead("3", "失敗したとき", "SDK のチェックでエラーが出たときなど"));
            policy = new Segmented(new[] { "飛ばして次へ（おすすめ）", "止める" }, S.stopOnError ? 1 : 0, i =>
            {
                S.stopOnError = i == 1;
                AvatarUploader.Save();
                Commit(i == 1 ? "失敗したら止める" : "失敗したら飛ばして次へ");
            });
            policy.style.alignSelf = Align.FlexStart;
            optCard.Add(policy);
            optCard.Add(UIKit.Text("飛ばしたアバターは、最後に理由といっしょに一覧で確認できます。「失敗したものをもう一度」で、そのアバターだけやり直せます。",
                "viah-faint", "viah-small", "viah-wrap", "viah-mt8"));
            c.Add(optCard);

            var queueCard = UIKit.Card();
            queueCard.name = "card-upload-queue";
            progressBox = UIKit.Div();
            queueBox = UIKit.Div();
            queueCard.Add(progressBox);
            queueCard.Add(queueBox);
            c.Add(queueCard);

            resultBox = UIKit.Div();
            c.Add(resultBox);

            footer = UIKit.Div("viah-footer-bar");
            page.Add(footer);

            AvatarUploader.Changed -= OnRunnerChanged;
            AvatarUploader.Changed += OnRunnerChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            // ログインの状態は SDK の画面で変わるので、ときどき見直す
            root.schedule.Execute(() =>
            {
                if (root.panel != null) RenderSdk();
            }).Every(2000);
            Render();
        }

        public override void OnHide()
        {
            AvatarUploader.Changed -= OnRunnerChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
        }

        void OnRunnerChanged()
        {
            if (root.panel != null) Render();
        }

        void OnHierarchyChanged()
        {
            // 行の「シーンに見つかりません」を更新する（アップロード中は進み具合の描き直しに任せる）
            if (root.panel != null && !S.running) RenderQueue();
        }

        VisualElement AvatarDropZone()
        {
            var zone = UIKit.Div("viah-dropzone");
            zone.name = "upload-drop";
            zone.Add(UIKit.Img("AvatarSelector", "viah-dropzone-icon"));
            zone.Add(UIKit.Text("ここにアバターをドラッグ＆ドロップ", "viah-dropzone-title"));
            zone.Add(UIKit.Text("一度アップロードしたことのあるアバター（Blueprint ID があるもの）を入れられます。最初の 1 回は SDK の画面でアップロードしてください。", "viah-dropzone-text"));
            List<GameObject> Dragged() => (DragAndDrop.objectReferences ?? Array.Empty<UnityEngine.Object>()).OfType<GameObject>().ToList();
            zone.RegisterCallback<DragEnterEvent>(_ => zone.AddToClassList("is-hover"));
            zone.RegisterCallback<DragLeaveEvent>(_ => zone.RemoveFromClassList("is-hover"));
            zone.RegisterCallback<DragExitedEvent>(_ => zone.RemoveFromClassList("is-hover"));
            zone.RegisterCallback<DragUpdatedEvent>(_ => DragAndDrop.visualMode = Dragged().Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected);
            zone.RegisterCallback<DragPerformEvent>(_ =>
            {
                zone.RemoveFromClassList("is-hover");
                var objs = Dragged();
                if (objs.Count == 0) return;
                DragAndDrop.AcceptDrag();
                Add(objs);
            });
            return zone;
        }

        void Add(List<GameObject> objects)
        {
            var problems = new List<string>();
            int added = AvatarUploader.Add(objects, problems);
            if (added > 0) Commit($"アップロードのキューに {added} 体追加");
            if (problems.Count == 0) return;
            if (problems.Count == 1 && added == 0)
            {
                window.Toast(problems[0], "warn");
                return;
            }
            var list = UIKit.Div("viah-conflict-list");
            foreach (var p in problems.Take(12))
            {
                var row = UIKit.Row("viah-conflict-item");
                row.Add(UIKit.Img("console.warnicon.sml"));
                row.Add(UIKit.Text(p, "viah-ml6", "viah-wrap", "viah-grow"));
                list.Add(row);
            }
            if (problems.Count > 12) list.Add(UIKit.Text($"ほか {problems.Count - 12} 件", "viah-faint", "viah-small"));
            window.ShowDialog(added > 0 ? $"{added} 体を追加しました（入れられないものがあります）" : "追加できませんでした", null, list,
                new ViahWindow.DialogButton("OK", null, "primary"));
        }

        void Render()
        {
            if (queueBox == null) return;
            RenderSdk();
            RenderProgress();
            RenderQueue();
            RenderResult();
            RenderFooter();
        }

        void RenderSdk()
        {
            if (sdkBox == null) return;
            sdkBox.Clear();
            if (B == null)
            {
                sdkBox.Add(UIKit.Hint(null, "VRChat SDK（Avatars）が入っていません",
                    "VCC で VRChat SDK - Avatars（3.7.0 以降）を入れたプロジェクトで使えます。", "warn"));
                return;
            }
            var grid = UIKit.Div("viah-upload-sdk");
            grid.Add(UIKit.KeyValue("SDK", "Avatars " + (string.IsNullOrEmpty(B.SdkVersion) ? "（バージョン不明）" : B.SdkVersion)));
            grid.Add(UIKit.KeyValue("ログイン", B.LoggedIn ? B.UserName : B.PanelOpen ? "ログインしていません" : "SDK の画面を開くと確認できます"));
            grid.Add(UIKit.KeyValue("アップロード先", B.Platform));
            sdkBox.Add(grid);
            sdkBox.Add(UIKit.Text("いまの Unity のビルド先に向けてアップロードします。Android（Quest）にも上げるときは、ビルド先を切り替えてからもう一度実行してください。",
                "viah-faint", "viah-small", "viah-wrap", "viah-mt4"));
            var row = UIKit.Row("viah-mt8", "viah-btn-group");
            var open = UIKit.Btn("SDK の画面を開く", () => B.OpenPanel(), "small");
            open.name = "btn-upload-sdk";
            row.Add(open);
            sdkBox.Add(row);
        }

        static (string text, string kind) StatusOf(UploadJob j)
        {
            switch (j.status)
            {
                case "preparing": return ("準備中", "rule");
                case "agreeing": return ("同意待ち（SDK の画面）", "warn");
                case "ready": return ("同意済み・順番待ち", "");
                case "building": return ("ビルド中", "rule");
                case "uploading": return ("アップロード中", "rule");
                case "done": return ("完了", "done");
                case "failed": return ("失敗", "danger");
                case "skipped": return ("飛ばしました", "");
                case "cancelled": return ("中止", "warn");
                case "interrupted": return ("中断（要確認）", "warn");
                default: return ("待機中", "");
            }
        }

        static string TimeText(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.Seconds} 秒";
        }

        void RenderProgress()
        {
            progressBox.Clear();
            int total = S.jobs.Count;
            var head = UIKit.Row("viah-queue-head");
            head.Add(UIKit.Text($"キュー（{total} 体）", "viah-section-title"));
            head.Add(UIKit.Spacer());
            if (total > 0)
            {
                var retry = UIKit.Btn("失敗したものをもう一度", RetryFailed, "small", "ghost");
                retry.SetEnabled(!S.running && S.jobs.Any(j => j.status == "failed"));
                head.Add(retry);
                var clearDone = UIKit.Btn("完了したものを消す", () =>
                {
                    S.jobs.RemoveAll(j => j.status == "done");
                    AvatarUploader.Save();
                    Commit("完了したものをキューから消す");
                }, "small", "ghost", "viah-ml4");
                clearDone.SetEnabled(!S.running && S.jobs.Any(j => j.status == "done"));
                head.Add(clearDone);
                var clearAll = UIKit.Btn("すべて消す", () =>
                {
                    S.jobs.Clear();
                    AvatarUploader.Save();
                    Commit("アップロードのキューをすべて消す");
                }, "small", "ghost", "viah-ml4");
                clearAll.SetEnabled(!S.running);
                head.Add(clearAll);
            }
            progressBox.Add(head);
            if (total == 0) return;

            int done = S.DoneCount;
            var current = S.jobs.Find(j => j.status == "building" || j.status == "uploading");
            float part = current != null && current.status == "uploading" && current.progress >= 0 ? 0.5f + current.progress * 0.5f : current != null ? 0.25f : 0;
            int pct = Mathf.RoundToInt((done + part) * 100f / total);
            var bar = UIKit.Div("viah-progress");
            var fill = UIKit.Div("viah-progress-fill");
            fill.style.width = Length.Percent(pct);
            bar.Add(fill);
            progressBox.Add(bar);
            var text = $"{done} / {total} 体が終わりました（{pct}%）";
            var agreeing = S.jobs.Find(j => j.status == "agreeing");
            if (agreeing != null) text += $"　・　SDK の画面で「{agreeing.name}」の著作権の同意（OK）を押してください";
            else if (current != null) text += $"　・　いま：{current.name}（{StatusOf(current).text}）";
            else if (S.jobs.Any(j => j.status == "preparing")) text += "　・　準備中";
            progressBox.Add(UIKit.Text(text, "viah-dim", "viah-small", "viah-mt4", "viah-wrap"));
        }

        void RetryFailed()
        {
            foreach (var j in S.jobs.Where(x => x.status == "failed"))
            {
                j.status = "waiting";
                j.message = "";
            }
            AvatarUploader.Save();
        }

        void RenderQueue()
        {
            if (queueBox == null) return;
            queueBox.Clear();
            if (S.jobs.Count == 0)
            {
                queueBox.Add(UIKit.Text("まだありません。上にアバターをドロップしてください。", "viah-faint", "viah-mt8"));
                return;
            }
            for (int i = 0; i < S.jobs.Count; i++) queueBox.Add(Row(S.jobs[i], i));
        }

        VisualElement Row(UploadJob j, int index)
        {
            var row = UIKit.Div("viah-queue-row");
            row.AddToClassList("is-" + j.status);
            var line = UIKit.Row("viah-queue-line");
            line.Add(UIKit.Text((index + 1).ToString(), "viah-queue-num"));
            line.Add(UIKit.Img("AvatarSelector", "viah-ml8"));
            var col = UIKit.Div("viah-grow", "viah-ml6");
            var title = UIKit.Row();
            title.Add(UIKit.Text(j.DisplayName, "viah-bold", "viah-ellipsis"));
            var st = StatusOf(j);
            title.Add(UIKit.Badge(st.text, st.kind));
            col.Add(title);

            var go = S.running ? null : AvatarUploader.Find(j);
            var info = $"{j.SceneName}　・　{j.blueprintId}";
            if (j.status == "done" && !string.IsNullOrEmpty(j.finishedAt)) info += $"　・　{j.finishedAt} に完了（{TimeText(j.seconds)}）";
            else if (!S.running && go == null && !j.Finished) info += "　・　シーンが開いていません（始めると開きます）";
            col.Add(UIKit.Text(info, "viah-faint", "viah-small", "viah-ellipsis"));

            if (j.Busy && !string.IsNullOrEmpty(j.phase))
            {
                col.Add(UIKit.Text(j.phase + (j.progress >= 0 ? $"（{Mathf.RoundToInt(j.progress * 100)}%）" : ""), "viah-small", "viah-ellipsis", "viah-mt4"));
                if (j.progress >= 0)
                {
                    var bar = UIKit.Div("viah-progress", "viah-progress-thin");
                    var fill = UIKit.Div("viah-progress-fill");
                    fill.style.width = Length.Percent(j.progress * 100);
                    bar.Add(fill);
                    col.Add(bar);
                }
            }
            if (!string.IsNullOrEmpty(j.message))
                col.Add(UIKit.Text(j.message, "viah-small", "viah-wrap", j.status == "failed" ? "viah-msg-error" : j.status == "done" ? "viah-faint" : "viah-msg-warn"));
            line.Add(col);
            line.tooltip = "クリックで Hierarchy のアバターを選択";
            line.RegisterCallback<ClickEvent>(_ =>
            {
                var target = AvatarUploader.Find(j);
                if (target == null)
                {
                    window.Toast("開いているシーンにこのアバターがありません", "warn");
                    return;
                }
                Selection.activeGameObject = target;
                EditorGUIUtility.PingObject(target);
            });

            bool movable = !S.running && !j.Busy;
            var up = UIKit.Btn("↑", () => Move(index, -1), "small", "ghost", "viah-ml8", "viah-move-up").With(b => b.tooltip = "上へ");
            up.SetEnabled(movable && index > 0);
            var down = UIKit.Btn("↓", () => Move(index, +1), "small", "ghost", "viah-move-down").With(b => b.tooltip = "下へ");
            down.SetEnabled(movable && index < S.jobs.Count - 1);
            var remove = UIKit.Btn("×", () =>
            {
                S.jobs.Remove(j);
                AvatarUploader.Save();
                Commit("アップロードのキューから外す");
            }, "small", "ghost").With(b => b.tooltip = "キューから外す（アバターは消えません）");
            remove.SetEnabled(!j.Busy);
            foreach (var btn in new[] { up, down, remove })
            {
                btn.RegisterCallback<ClickEvent>(e => e.StopPropagation());
                line.Add(btn);
            }
            row.Add(line);
            return row;
        }

        void Move(int index, int dir)
        {
            int to = index + dir;
            if (to < 0 || to >= S.jobs.Count) return;
            var j = S.jobs[index];
            S.jobs.RemoveAt(index);
            S.jobs.Insert(to, j);
            AvatarUploader.Save();
            Commit("アップロードのキューを並べ替え");
        }

        void RenderResult()
        {
            resultBox.Clear();
            if (S.running || !S.Complete) return;
            int ok = S.jobs.Count(j => j.status == "done");
            int failed = S.jobs.Count(j => j.status == "failed");
            int skipped = S.jobs.Count(j => j.status == "skipped");
            var card = failed > 0 ? UIKit.Card() : UIKit.Card("accent");
            card.name = "card-upload-result";
            var sub = $"アップロード {ok} 体" + (failed > 0 ? $"・失敗 {failed} 体" : "") + (skipped > 0 ? $"・飛ばした {skipped} 体" : "") +
                      $"（合計 {TimeText(S.jobs.Sum(j => j.seconds))}）";
            card.Add(UIKit.SectionHead(failed > 0 ? "!" : "✓", failed > 0 ? "終わりました（失敗したものがあります）" : "すべて終わりました", sub));
            foreach (var j in S.jobs.Where(x => x.status != "done"))
            {
                var row = UIKit.Row("viah-check-item");
                var dot = UIKit.Div("viah-status-dot");
                dot.AddToClassList(j.status == "failed" ? "danger" : "warn");
                row.Add(dot);
                var col = UIKit.Div("viah-grow");
                col.Add(UIKit.Text(j.DisplayName, "viah-bold"));
                col.Add(UIKit.Text(j.message, "viah-faint", "viah-small", "viah-wrap"));
                row.Add(col);
                card.Add(row);
            }
            if (failed > 0)
            {
                var actions = UIKit.Row("viah-mt8", "viah-btn-group");
                actions.Add(UIKit.Btn("失敗したものをもう一度", RetryFailed, "small", "primary"));
                actions.Add(UIKit.Btn("SDK の画面を開く", () => B?.OpenPanel(), "small", "viah-ml4"));
                card.Add(actions);
            }
            resultBox.Add(card);
        }

        void RenderFooter()
        {
            footer.Clear();
            if (S.running)
            {
                var agreeing = S.jobs.Find(j => j.status == "agreeing");
                string note = S.stopAfterCurrent ? "いまのアバターが終わったら止めます。"
                    : agreeing != null ? $"SDK の画面で「{agreeing.name}」の著作権の同意（OK）を押してください。全員分が終わると、あとは自動で進みます。"
                    : "アップロード中です。終わるまで Unity の操作は控え、SDK の画面は閉じないでください。";
                footer.Add(UIKit.Text(note, "viah-grow", "viah-wrap", agreeing != null ? "viah-msg-warn" : "viah-faint"));
                var stop = UIKit.Btn("止める", () => window.ShowDialog("止めますか？",
                    "「今すぐ中止」はアップロード中のものを取り消します（ビルド中のものは、ビルドが終わってから止まります）。", null,
                    new ViahWindow.DialogButton("続ける", null, "ghost"),
                    new ViahWindow.DialogButton("いまのが終わったら止める", AvatarUploader.StopAfterCurrent),
                    new ViahWindow.DialogButton("今すぐ中止", AvatarUploader.Abort, "danger")), "big");
                stop.name = "btn-upload-stop";
                footer.Add(stop);
                return;
            }
            if (S.Complete)
            {
                footer.Add(UIKit.Text("", "viah-grow"));
                footer.Add(UIKit.Btn("キューを空にする", () =>
                {
                    S.jobs.Clear();
                    AvatarUploader.Save();
                    Commit("アップロードのキューを空にする");
                }, "big"));
                return;
            }
            var reason = AvatarUploader.CannotStart();
            var notice = !string.IsNullOrEmpty(S.notice) ? S.notice : reason ?? "";
            footer.Add(UIKit.Text(notice, "viah-grow", "viah-wrap", !string.IsNullOrEmpty(S.notice) ? "viah-msg-warn" : "viah-faint"));
            bool resumed = S.jobs.Any(j => j.Finished || j.status == "cancelled" || j.status == "interrupted");
            var start = UIKit.Btn(resumed ? "続きを開始 ▶" : "アップロード開始 ▶", () => AvatarUploader.Start(), "primary", "big");
            start.name = "btn-upload-start";
            start.SetEnabled(reason == null);
            footer.Add(start);
        }
    }
}
