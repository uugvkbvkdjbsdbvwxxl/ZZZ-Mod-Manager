using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;

namespace ZZZModManager.Tests;

public sealed class ScrollBarStyleTests
{
    [Fact]
    public void ImplicitStyleSizesScrollBarsByOrientation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dictionary = Assert.IsType<ResourceDictionary>(Application.LoadComponent(
                    new Uri("/ZZZModManager;component/Themes/DarkTheme.xaml", UriKind.Relative)));
                var style = Assert.IsType<Style>(dictionary[typeof(ScrollBar)]);

                var horizontal = new ScrollBar
                {
                    Orientation = Orientation.Horizontal,
                    Style = style
                };
                var vertical = new ScrollBar
                {
                    Orientation = Orientation.Vertical,
                    Style = style
                };

                Assert.True(double.IsNaN(horizontal.Width));
                Assert.Equal(10d, horizontal.Height);
                Assert.Equal(10d, vertical.Width);
                Assert.True(double.IsNaN(vertical.Height));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF style verification timed out.");
        Assert.Null(failure);
    }
}
