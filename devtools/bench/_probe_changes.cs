using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// 临时探针：把「本轮的改动」对话框渲染成 PNG（让窗口自绘，不截屏、不受别的窗口影响），
// 用来肉眼核对修完之后的排版和对色。
class ProbeChanges
{
    [STAThread]
    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "changes.png";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        List<FileChange> files = new List<FileChange>();
        files.Add(Make("src\\AhzScribe.cs", OldText(), NewText()));
        files.Add(Make("给Codex的信-2026-10-13.md", "", "这是新加的一封信\n第二行内容\n第三行内容\n"));

        Form f = new ChangesForm(files, 0);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(0, 0);
        f.TopMost = true;                       // 保证盖在别的窗口上面，好做真屏幕截图
        f.Show();
        for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(outPath, ImageFormat.Png);
        }
        // RichTextBox 用 DrawToBitmap 画不出文字（WinForms 的老毛病），所以再抓一张真屏幕
        string shotPath = outPath.Replace(".png", "-screen.png");
        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size);
            bmp.Save(shotPath, ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + outPath + "  尺寸 " + f.Width + "x" + f.Height);
        Console.WriteLine("已保存 " + shotPath);
        f.Close();
    }

    static FileChange Make(string rel, string oldText, string newText)
    {
        FileChange c = new FileChange();
        c.Rel = rel;
        c.Path = Path.Combine(Path.GetTempPath(), "probe-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
        c.OldText = oldText;
        c.NewText = newText;
        return c;
    }

    static string OldText()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 24; i++) sb.Append("    static void Step").Append(i).Append("() { /* 第 ").Append(i).Append(" 步的旧实现 */ }\n");
        return sb.ToString();
    }

    static string NewText()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 24; i++)
        {
            if (i == 6) sb.Append("    static void Step6() { /* 这一行被改过 */ }\n");
            else if (i == 12) sb.Append("    static void Step12() { /* 这一行也被改过，而且很长很长很长很长很长很长很长很长很长很长 */ }\n");
            else sb.Append("    static void Step").Append(i).Append("() { /* 第 ").Append(i).Append(" 步的旧实现 */ }\n");
        }
        return sb.ToString();
    }
}
