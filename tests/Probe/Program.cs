using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using BvdsForWindows.Views;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        var app = new BvdsForWindows.App();
        app.InitializeComponent();

        var v = new HomeView();
        v.Loaded += (s, e) =>
        {
            Console.WriteLine(">>> Loaded fired");
            var anim = new DoubleAnimation();
            anim.From = 0; anim.To = 1; anim.Duration = TimeSpan.FromMilliseconds(300);
            v.BeginAnimation(UIElement.OpacityProperty, anim);
        };
        var win = new Window { Width = 500, Height = 400, Title = "anim-probe", Content = v };
        win.Show();
        win.Activate();
        System.Threading.Thread.Sleep(120);
        Console.WriteLine("120ms Opacity: " + v.Opacity.ToString("F2"));
        System.Threading.Thread.Sleep(400);
        Console.WriteLine("500ms Opacity: " + v.Opacity.ToString("F2"));
        return 0;
    }
}
