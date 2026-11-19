using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;



namespace Skladka
{
    class Desktop
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Studio());
        }
    }

    static class Ink
    {
        public static readonly Color Paper = Color.FromArgb(248, 248, 242);
        public static readonly Color Dark = Color.FromArgb(27, 48, 41);
        public static readonly Color Muted = Color.FromArgb(82, 101, 90);
        public static readonly Color Mint = Color.FromArgb(230, 237, 223);
        public static readonly Color Lime = Color.FromArgb(213, 243, 138);
        public static readonly Color Line = Color.FromArgb(196, 207, 192);
        public static Font Font(float size, bool bold)
        {
            return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        }
        public static Font CanvasFont(float size, bool bold)
        {
            return new Font("Segoe UI", size * 96f / 72f, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void Sheets(Graphics g, float x, float y, float scale)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(scale, scale);
            using (var outline = new Pen(Dark, 2.3f))
            {
                outline.LineJoin = LineJoin.Round;
                for (int layer = 2; layer >= 0; layer--)
                {
                    int dy = layer * 15;
                    Point[] corners = { new Point(0, 31+dy), new Point(64, dy), new Point(121, 26+dy), new Point(57, 59+dy) };
                    using (var fill = new SolidBrush(layer == 0 ? Lime : layer == 1 ? Color.FromArgb(163, 193, 153) : Dark))
                        g.FillPolygon(fill, corners);
                    g.DrawPolygon(outline, corners);
                }
                g.DrawLine(outline, 30, 29, 62, 14);
                g.DrawLine(outline, 39, 33, 60, 23);
            }
            g.Restore(state);
        }
    }

    class ActionButton : Button
    {
        public Color Fill = Ink.Dark, Tint = Ink.Paper;
        bool hover;
        public ActionButton()
        {
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            Font = Ink.Font(10, true);
            DoubleBuffered = true;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = !Enabled ? Color.FromArgb(219, 225, 214) : hover ? ControlPaint.Dark(Fill, .07f) : Fill;
            using (var shape = Ink.Round(new RectangleF(1, 1, Width - 2, Height - 2), 10))
            using (var fill = new SolidBrush(color)) g.FillPath(fill, shape);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Tint : Ink.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -6, -6), Tint, color);
        }
    }

    class DropZone : Button
    {
        public string SelectedName, Description;
        public bool Extract, Dragging;
        public DropZone()
        {
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            AccessibleName = "Выбрать файл";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = g.DpiX / 96f;
            g.ScaleTransform(s, s);
            float w = Width/s, h = Height/s;
            using (var shape = Ink.Round(new RectangleF(1, 1, w-3, h-3), 18))
            using (var fill = new SolidBrush(Dragging ? Ink.Lime : Ink.Mint))
            using (var pen = new Pen(Focused || Dragging ? Ink.Dark : Ink.Line, 2))
            {
                pen.DashStyle = DashStyle.Dash;
                g.FillPath(fill, shape);
                g.DrawPath(pen, shape);
            }
            float middle = h/2;
            Ink.Sheets(g, w-170, middle-48, 1.06f);
            using (var title = Ink.CanvasFont(17, true))
            using (var small = Ink.CanvasFont(10, false))
            using (var link = Ink.CanvasFont(10, true))
            using (var dark = new SolidBrush(Ink.Dark))
            using (var muted = new SolidBrush(Ink.Muted))
            using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(SelectedName ?? (Extract ? "Перетащите архив сюда" : "Перетащите файл или папку"), title, dark,
                    new RectangleF(28, middle-44, w-215, 34), fmt);
                g.DrawString(Description ?? (Extract ? "Архив в формате .skl" : "Файл любого формата"), small, muted,
                    new RectangleF(29, middle-4, w-218, 24), fmt);
                g.DrawString(SelectedName == null ? "Выбрать на компьютере  →" : "Выбрать другой файл  →", link, dark, 28, middle+24);
            }
        }
    }

    class Studio : Form
    {
        readonly Label brand, title, intro, modeLabel, status, detail;
        readonly ActionButton pack, unpack, action, folderPick;
        readonly DropZone drop;
        readonly ProgressBar progress;
        bool extract, busy, failed;
        string path, resultName;
        FileStream result;
        FolderBundle restoredFolder;
        bool sourceIsFolder;
        float scale;

        public Studio()
        {
            Text = "Складка — архиватор";
            using (var ownIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ownIcon != null) Icon = (Icon)ownIcon.Clone();
            BackColor = Ink.Paper;
            ForeColor = Ink.Dark;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size(D(850), D(625));
            MinimumSize = SizeFromClientSize(new Size(D(625), D(570)));
            StartPosition = FormStartPosition.CenterScreen;
            brand = LabelOf("складка", 19, true);
            title = LabelOf("Архиватор файлов", 31, true);
            intro = LabelOf("Сжимайте и восстанавливайте файлы на своём компьютере.", 11, false);
            intro.ForeColor = Ink.Muted;
            modeLabel = LabelOf("Операция", 10, true);
            pack = Btn("Сжать");
            unpack = Btn("Распаковать");
            pack.Click += delegate { ChangeMode(false); };
            unpack.Click += delegate { ChangeMode(true); };
            folderPick = Btn("Выбрать папку");
            folderPick.Fill=Ink.Mint;folderPick.Tint=Ink.Dark;
            folderPick.Click += delegate
            {
                using(var dialog=new FolderBrowserDialog {Description="Выберите папку для архива",ShowNewFolderButton=false})
                    if(dialog.ShowDialog(this)==DialogResult.OK) SelectPath(dialog.SelectedPath);
            };
            drop = new DropZone { AllowDrop = true };
            Controls.Add(drop);
            drop.Click += delegate { Pick(); };
            drop.DragEnter += delegate(object sender, DragEventArgs e)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                drop.Dragging = !busy && files != null && files.Length == 1 &&
                    (File.Exists(files[0]) || (!extract && Directory.Exists(files[0])));
                e.Effect = drop.Dragging ? DragDropEffects.Copy : DragDropEffects.None;
                drop.Invalidate();
            };
            drop.DragLeave += delegate { drop.Dragging = false; drop.Invalidate(); };
            drop.DragDrop += delegate(object sender, DragEventArgs e)
            {
                drop.Dragging = false;
                drop.Invalidate();
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (!busy && files != null && files.Length == 1 && (!extract || File.Exists(files[0]))) SelectPath(files[0]);
            };
            status = LabelOf("", 13, true);
            detail = LabelOf("", 10, false);
            detail.ForeColor = Ink.Muted;
            action = Btn("");
            action.Click += async delegate
            {
                if(restoredFolder!=null) await SaveFolder();
                else if(result!=null) await SaveResult();
                else await ProcessFile();
            };
            progress = new ProgressBar { Visible = false };
            Controls.Add(progress);
            Resize += delegate { Arrange(); };
            ShowState();
            Arrange();
        }
        int D(float value) { return (int)Math.Round(value*scale); }
        Label LabelOf(string text, float size, bool bold)
        {
            var l = new Label { Text = text, Font = Ink.Font(size,bold), AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            Controls.Add(l);
            return l;
        }
        ActionButton Btn(string text)
        {
            var b = new ActionButton { Text = text };
            Controls.Add(b);
            return b;
        }
        void At(Control c, int x, int y, int w, int h) { c.SetBounds(D(x), D(y), D(w), D(h)); }
        void Arrange()
        {
            int w = (int)(ClientSize.Width/scale), h = (int)(ClientSize.Height/scale);
            int left = Math.Max(32, (w-820)/2), width = w-2*left;
            At(brand,left+45,12,160,48);
            At(title,left,91,width,64);
            At(intro,left+2,156,width,32);
            At(modeLabel,left,210,110,40);
            At(pack,left+109,209,110,40);
            At(unpack,left+225,209,130,40);
            At(folderPick,left+width-151,209,151,40);
            At(drop,left,269,width,h-413);
            At(progress,left,h-135,width,5);
            At(status,left,h-120,width-210,34);
            At(detail,left,h-85,width-212,52);
            At(action,left+width-194,h-104,194,50);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (scale <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(scale,scale);
            int w = (int)(ClientSize.Width/scale), left = Math.Max(32,(w-820)/2);
            Ink.Sheets(g,left+1,25,.30f);
            using (var pen = new Pen(Ink.Line)) g.DrawLine(pen,0,74,w,74);
            using (var fill = new SolidBrush(Ink.Dark))
            using (var font = Ink.CanvasFont(9,false))
            {
                g.FillEllipse(fill,w-left-90,33,7,7);
                g.DrawString("Локально",font,fill,w-left-76,27);
            }
        }
        void ClearResult()
        {
            if (result != null) result.Dispose();
            result = null;
            restoredFolder=null;
            failed = false;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) ClearResult();
            base.Dispose(disposing);
        }
        void ChangeMode(bool next)
        {
            if (busy || extract == next) return;
            extract = next;
            path = null;
            ClearResult();
            ShowState();
        }
        void Pick()
        {
            if (busy) return;
            using (var dialog = new OpenFileDialog { Filter = extract ? "Архив Складки (*.skl)|*.skl|Все файлы|*.*" : "Все файлы|*.*" })
                if (dialog.ShowDialog(this) == DialogResult.OK) SelectPath(dialog.FileName);
        }
        void SelectPath(string file)
        {
            ClearResult();
            path = file;
            sourceIsFolder=Directory.Exists(file);
            sourceSize=0;
            ShowState();
        }
        static string SizeText(long n)
        {
            if (n < 1024) return n + " Б";
            if (n < 1048576) return (n/1024.0).ToString("0.#") + " КиБ";
            if (n < 1073741824L) return (n/1048576.0).ToString("0.##") + " МиБ";
            return (n/1073741824.0).ToString("0.##") + " ГиБ";
        }
        long sourceSize;
        void ShowState()
        {
            pack.Fill = extract ? Ink.Mint : Ink.Dark;
            unpack.Fill = extract ? Ink.Dark : Ink.Mint;
            pack.Tint = extract ? Ink.Dark : Ink.Paper;
            unpack.Tint = extract ? Ink.Paper : Ink.Dark;
            pack.Enabled = unpack.Enabled = drop.Enabled = !busy;
            folderPick.Visible=!extract;
            folderPick.Enabled=!busy;
            drop.Extract = extract;
            drop.SelectedName = path == null ? null : Path.GetFileName(path);
            drop.Description = null;
            if (path != null && sourceIsFolder) drop.Description="Папка со вложенными файлами";
            else if (path != null)
            {
                try { sourceSize = new FileInfo(path).Length; drop.Description = SizeText(sourceSize); }
                catch (IOException) { drop.Description = "Файл недоступен"; }
                catch (UnauthorizedAccessException) { drop.Description = "Нет доступа к файлу"; }
            }
            status.ForeColor = failed ? Color.Firebrick : Ink.Dark;
            if (!failed)
            {
                status.Text = result != null ? (restoredFolder!=null ? "Папка готова к сохранению" : extract ? "Файл восстановлен" : "Архив готов") :
                    busy ? (extract ? "Распаковываем…" : "Сжимаем…") : path == null ? "Файл не выбран" : "Готово к обработке";
                detail.Text = result != null ? SizeText(sourceSize) + "  →  " + SizeText(restoredFolder!=null ? restoredFolder.Size : result.Length) +
                    (extract ? "  ·  SHA-256 совпадает" : sourceSize > result.Length ? "  ·  экономия " + ((1-result.Length/(double)sourceSize)*100).ToString("0.#") + " %" : "  ·  с учётом заголовка") :
                    path == null ? "Выберите " + (extract ? "архив для распаковки." : "файл для сжатия.") :
                    busy ? "Обрабатываем данные по блокам." : extract ? "При распаковке проверим SHA-256." : "Результат сохранится в формате .skl.";
            }
            action.Text = result != null ? "Сохранить " + (restoredFolder!=null ? "папку" : extract ? "файл" : "архив") + "  ↓" : (extract ? "Распаковать" : sourceIsFolder && path!=null ? "Сжать папку" : "Сжать файл") + "  →";
            action.Fill = result == null ? Ink.Dark : Ink.Lime;
            action.Tint = result == null ? Ink.Paper : Ink.Dark;
            action.Enabled = !busy && path != null;
            progress.Visible = busy;
            drop.Invalidate();
            pack.Invalidate(); unpack.Invalidate(); action.Invalidate();
        }
        static string SafeName(string candidate)
        {
            if (String.IsNullOrWhiteSpace(candidate) || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                candidate == "." || candidate == ".." || candidate.TrimEnd(' ','.') != candidate) return "result.bin";
            return candidate;
        }
        async Task ProcessFile()
        {
            if (busy || path == null) return;
            busy = true;
            failed = false;
            progress.Value = 0;
            ShowState();
            FileStream dest = null;
            IProgress<int> report = new Progress<int>(p => { if (!IsDisposed) progress.Value = p; });
            try
            {
                dest = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                    FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                await Task.Run(delegate
                {
                    if(!extract && sourceIsFolder)
                    {
                        sourceSize=FolderBundle.Pack(path,dest,report.Report);
                        resultName=new DirectoryInfo(path).Name+".skl";
                        return;
                    }
                    using (var source = File.OpenRead(path))
                    {
                        if (extract)
                        {
                            bool folder;
                            resultName=SafeName(Archive.Unpack(source,dest,report.Report,out folder));
                            if(folder)restoredFolder=FolderBundle.Read(dest);
                        }
                        else
                        {
                            Archive.Pack(source, dest, Path.GetFileName(path), report.Report);
                            resultName = Path.GetFileName(path) + ".skl";
                        }
                    }
                });
                if (IsDisposed) dest.Dispose(); else result = dest;
            }
            catch (Exception ex)
            {
                if (dest != null) dest.Dispose();
                if (IsDisposed) return;
                failed = true;
                status.Text = extract ? "Не удалось распаковать" : "Не удалось сжать";
                detail.Text = ex is EndOfStreamException || ex is System.Text.DecoderFallbackException ? "Повреждённый заголовок архива." : ex.Message;
            }
            finally { busy = false; if (!IsDisposed) ShowState(); }
        }
        async Task SaveFolder()
        {
            using(var dialog=new FolderBrowserDialog {Description="Где создать восстановленную папку?"})
            {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                string parent=dialog.SelectedPath;
                busy=true;ShowState();status.Text="Сохраняем папку…";
                try
                {
                    string saved=await Task.Run(()=>restoredFolder.Save(result,parent));
                    if(!IsDisposed){status.Text="Папка сохранена";detail.Text=saved;}
                }
                catch(Exception ex){if(!IsDisposed){status.Text="Не удалось сохранить папку";detail.Text=ex.Message;}}
                finally
                {
                    busy=false;
                    if(!IsDisposed){pack.Enabled=unpack.Enabled=drop.Enabled=action.Enabled=folderPick.Enabled=true;progress.Visible=false;}
                }
            }
        }
        async Task SaveResult()
        {
            using (var dialog = new SaveFileDialog { FileName = resultName, Filter = extract ? "Все файлы|*.*" : "Архив Складки (*.skl)|*.skl", AddExtension = !extract })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string temp = null;
                bool created = false;
                try
                {
                    busy = true;
                    ShowState();
                    status.Text = "Сохраняем файл…";
                    if (String.Equals(Path.GetFullPath(path),Path.GetFullPath(dialog.FileName),StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Выберите другое имя, чтобы сохранить исходный файл.");
                    temp = Path.Combine(Path.GetDirectoryName(dialog.FileName), Path.GetRandomFileName());
                    using (var stream = new FileStream(temp,FileMode.CreateNew,FileAccess.Write))
                    {
                        created = true;
                        result.Position = 0;
                        await result.CopyToAsync(stream);
                    }
                    if (File.Exists(dialog.FileName)) File.Replace(temp,dialog.FileName,null);
                    else File.Move(temp,dialog.FileName);
                    temp = null;
                    status.Text = "Сохранено";
                    detail.Text = dialog.FileName;
                }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                    {
                        status.Text = "Не удалось сохранить файл";
                        detail.Text = ex.Message;
                        MessageBox.Show(this,ex.Message,"Сохранение",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    }
                }
                finally
                {
                    busy = false;
                    if (!IsDisposed)
                    {
                        pack.Enabled = unpack.Enabled = drop.Enabled = action.Enabled = folderPick.Enabled = true;
                        progress.Visible = false;
                    }
                    if (created && temp != null)
                    {
                        try { File.Delete(temp); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }
    }
}
