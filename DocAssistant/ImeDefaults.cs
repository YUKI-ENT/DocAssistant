using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DocAssistant;

internal static class ImeDefaults
{
    internal static void Initialize()
    {
        foreach (var type in new[] { typeof(TextBoxBase), typeof(ComboBox) })
        {
            EventManager.RegisterClassHandler(type, FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => Apply((DependencyObject)sender)));
            EventManager.RegisterClassHandler(type, Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((sender, e) =>
                {
                    if (!ReferenceEquals(sender, e.NewFocus)) return;
                    var target = (DependencyObject)sender;
                    var state = Apply(target);
                    if (InputMethod.GetIsInputMethodEnabled(target)) InputMethod.Current.ImeState = state;
                }), true);
        }
    }

    private static InputMethodState Apply(DependencyObject target)
    {
        var state = InputMethodState.On;
        for (DependencyObject? current = target; current != null; current = Parent(current))
        {
            if (current is DatePicker or DatePickerTextBox ||
                current.ReadLocalValue(InputMethod.PreferredImeStateProperty) is InputMethodState.Off)
            { state = InputMethodState.Off; break; }
        }
        InputMethod.SetPreferredImeState(target, state);
        return state;
    }

    private static DependencyObject? Parent(DependencyObject element) =>
        element is Visual ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
