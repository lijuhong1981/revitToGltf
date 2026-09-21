using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RevitToGltf.Commands
{
    /// <summary>
    /// 导出进度窗体（无模式）。
    ///
    /// 提取/写出必须在 Revit 主线程上同步执行（Revit API 不是线程安全的），所以无法把工作搬到
    /// 后台线程去。这里采用无模式窗体 + 在每次进度回调里调用 Application.DoEvents() 泵消息的方式，
    /// 让窗体保持重绘、已用时持续走动、并且「取消」按钮可以点击。
    /// </summary>
    internal class ExportProgressForm : System.Windows.Forms.Form
    {
        private readonly Label _phaseLabel;
        private readonly Label _detailLabel;
        private readonly ProgressBar _bar;
        private readonly Button _cancelButton;
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        /// <summary>用户点击「取消」后为 true，由导出循环轮询</summary>
        public volatile bool Cancelled;

        public ExportProgressForm(string title)
        {
            Text = title;
            ClientSize = new Size(660, 186);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;

            _phaseLabel = new Label { Text = "准备中…", Left = 16, Top = 20, Width = 620, Height = 26 };
            _bar = new ProgressBar { Left = 16, Top = 56, Width = 628, Height = 28, Minimum = 0, Maximum = 100 };
            _detailLabel = new Label { Text = "", Left = 16, Top = 94, Width = 620, Height = 26 };

            _cancelButton = new Button { Text = "取消", Left = 532, Top = 132, Width = 112, Height = 38 };
            _cancelButton.Click += (s, e) =>
            {
                Cancelled = true;
                _cancelButton.Enabled = false;
                _phaseLabel.Text = "正在取消，请稍候…";
                Application.DoEvents();
            };

            Controls.Add(_phaseLabel);
            Controls.Add(_bar);
            Controls.Add(_detailLabel);
            Controls.Add(_cancelButton);
        }

        /// <summary>更新进度并泵一次消息（保持窗体可重绘、取消可点击）</summary>
        public void Report(int percent, string message)
        {
            if (IsDisposed) return;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            _bar.Value = percent;
            _phaseLabel.Text = message;
            _detailLabel.Text = ElapsedText();
            Application.DoEvents();
        }

        private string ElapsedText()
        {
            TimeSpan elapsed = _watch.Elapsed;
            return string.Format("已用时 {0:00}:{1:00}:{2:00}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
        }
    }
}
