using System.Windows;

namespace Taildesk.Admin;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueText.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueText.Focus();
            ValueText.SelectAll();
        };
    }

    public string Value => ValueText.Text;
    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
