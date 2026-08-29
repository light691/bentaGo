using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Regression for the review-gate finding: FocusVisual.Default resolved to a
/// raw ControlTemplate while FocusVisualStyle is a Style-typed property, so
/// the first REAL keyboard navigation onto a styled control threw
/// "'ControlTemplate' is not a valid value for property 'FocusVisualStyle'"
/// and the app showed its error dialog on an empty screen. Programmatic
/// Focus() and soft MoveFocus do NOT show the focus visual, which is why the
/// smoke/layout probes and the L5 lane missed it. This test drives real
/// keyboard input (ALT for focus cues + Tab, posted into the WPF HwndSource
/// exactly like OS input) onto a styled control in a shown window and
/// asserts no exception surfaces on the dispatcher.
/// </summary>
[Collection("AppShellSta")]
public sealed class FocusVisualStyleRegressionTests
{
    private readonly AppShellStaFixture _sta;

    public FocusVisualStyleRegressionTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void KeyboardTabOntoStyledControlAppliesFocusVisualWithoutException()
    {
        var captured = new List<Exception>();
        _sta.Run(() =>
        {
            var window = new Window
            {
                Width = 640,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 40,
                ShowInTaskbar = false,
            };
            DispatcherUnhandledExceptionEventHandler? dispatcherGuard = null;
            try
            {
                dispatcherGuard = (_, e) =>
                {
                    captured.Add(e.Exception);
                    e.Handled = true;
                };
                System.Windows.Application.Current.DispatcherUnhandledException += dispatcherGuard;

                var box = new TextBox { Width = 300, MinHeight = 38, Margin = new Thickness(12) };
                _ = new Button { Content = "Next", Width = 120, MinHeight = 44, Margin = new Thickness(12) };
                window.Content = new StackPanel { Children = { box } };
                window.Show();
                _sta.PumpIdle();
                window.Activate();
                _sta.PumpIdle();
                Thread.Sleep(150);

                // Programmatic focus first: safe even pre-fix, because the
                // FocusVisualStyle is only consumed on REAL keyboard input.
                box.Focus();
                Keyboard.Focus(box);
                _sta.PumpIdle();
                Assert.True(ReferenceEquals(Keyboard.FocusedElement, box), "probe window must own focus first");

                // Real keyboard input delivered straight into the WPF
                // HwndSource (WM_SYSKEYDOWN for ALT shows WPF's keyboard
                // focus cues; WM_KEYDOWN Tab walks focus): the exact path the
                // operator's pass hit. Any exception surfaces on the
                // dispatcher and is captured by the guard.
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                for (int i = 0; i < 3 && captured.Count == 0; i++)
                {
                    NativeKey.Post(hwnd, VkMenu);
                    NativeKey.Post(hwnd, VkTab);
                    _sta.PumpIdle();
                    Thread.Sleep(120);
                }
            }
            catch (Exception ex)
            {
                captured.Add(ex);
            }
            finally
            {
                if (dispatcherGuard is not null)
                {
                    System.Windows.Application.Current.DispatcherUnhandledException -= dispatcherGuard;
                }

                window.Close();
            }
        });

        Assert.True(
            captured.Count == 0,
            "focus visual applied with a type error:\n" + string.Join("\n", captured.Select(e => e.ToString())));
    }

    private const ushort VkMenu = 0x12;
    private const ushort VkTab = 0x09;

    private static class NativeKey
    {
        private const uint WmKeyDown = 0x0100;
        private const uint WmKeyUp = 0x0101;
        private const uint WmSysKeyDown = 0x0104;
        private const uint WmSysKeyUp = 0x0105;

        public static void Post(IntPtr hwnd, ushort vk)
        {
            bool system = vk == VkMenu;
            _ = PostMessage(hwnd, system ? WmSysKeyDown : WmKeyDown, new IntPtr(vk), TabLParam);
            Thread.Sleep(15);
            _ = PostMessage(hwnd, system ? WmSysKeyUp : WmKeyUp, new IntPtr(vk), TabLParamUp);
        }

        // lParam: repeat count 1, scan code 0x0F (Tab); key-up sets the
        // previous-key-state bit.
        private static readonly IntPtr TabLParam = new(0x000F0001);
        private static readonly IntPtr TabLParamUp = new(0xC00F0001);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}