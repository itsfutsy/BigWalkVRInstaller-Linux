using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace BigWalkVRInstaller
{
    public partial class SupportFooter : UserControl
    {
        static readonly string[] ConfettiColors = { "Support", "AmberHover", "Green", "Accent", "Amber", "SupportHover" };
        static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(0.8);
        readonly Random _random = new Random();
        Point? _pointer;

        public event EventHandler SupportClicked;

        public SupportFooter()
        {
            InitializeComponent();
            // the button swallows the press, so follow the pointer to know where the burst starts
            AddHandler(PointerMovedEvent, (_, e) => _pointer = e.GetPosition(Confetti), RoutingStrategies.Bubble, true);
        }

        void Support_Click(object sender, RoutedEventArgs e)
        {
            // keyboard presses have no pointer, burst from the middle of the button instead
            var origin = _pointer ?? SupportButton.TranslatePoint(new Point(SupportButton.Bounds.Width / 2, SupportButton.Bounds.Height / 2), Confetti) ?? default;
            Burst(origin);
            SupportClicked?.Invoke(this, EventArgs.Empty);
        }

        void Burst(Point origin)
        {
            var pieces = new List<(Rectangle shape, double dx, double dy, double spin)>();
            for (var i = 0; i < 22; i++)
            {
                var angle = _random.NextDouble() * Math.PI * 2;
                var distance = 40 + _random.NextDouble() * 50;
                var piece = new Rectangle
                {
                    Width = 5, Height = 8, RadiusX = 1, RadiusY = 1,
                    Fill = this.TryFindResource(ConfettiColors[i % ConfettiColors.Length], out var brush) ? brush as IBrush : Brushes.White,
                    RenderTransformOrigin = RelativePoint.Center,
                };
                Confetti.Children.Add(piece);
                pieces.Add((piece, Math.Cos(angle) * distance, Math.Sin(angle) * distance - 30, _random.NextDouble() * 720 - 360));
            }

            var clock = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / BurstDuration.TotalMilliseconds);
                var eased = 1 - Math.Pow(1 - t, 3); // cubic ease out
                foreach (var (shape, dx, dy, spin) in pieces)
                {
                    Canvas.SetLeft(shape, origin.X - 2.5 + dx * eased);
                    Canvas.SetTop(shape, origin.Y - 4 + dy * eased);
                    shape.RenderTransform = new RotateTransform(spin * eased);
                    shape.Opacity = 1 - eased;
                }
                if (t < 1) return;
                timer.Stop();
                foreach (var piece in pieces) Confetti.Children.Remove(piece.shape);
            };
            timer.Start();
        }
    }
}
