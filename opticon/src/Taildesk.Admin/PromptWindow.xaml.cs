using System.Windows;

namespace Taildesk.Admin;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Loaded += (_, _) => ValueText.Focus();
    }

    public string Value => ValueText.Text;
    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
