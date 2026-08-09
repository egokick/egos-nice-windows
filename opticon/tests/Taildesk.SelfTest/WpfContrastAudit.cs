using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Taildesk.SelfTest;

internal static class WpfContrastAudit
{
    // WCAG 2.x relative luminance. Enforcing the normal-text threshold for
    // every font size also keeps future typography changes safe.
    private const double MinimumContrast = 4.5;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> TextElements = new(StringComparer.Ordinal)
    {
        "TextBlock", "Run", "TextBox", "PasswordBox", "Button", "CheckBox", "RadioButton",
        "Label", "ComboBox", "ComboBoxItem", "ListBoxItem", "MenuItem", "Expander",
        "DataGridTextColumn", "DataGridColumnHeader", "DataGridCell", "DataGridRow"
    };

    private static readonly HashSet<string> MetadataElements = new(StringComparer.Ordinal)
    {
        "Application.Resources", "Window.Resources", "Grid.Resources", "DataGrid.Resources",
        "DataGrid.Columns", "DataGrid.ContextMenu", "DataGrid.RowStyle", "Style", "Setter",
        "Style.Setters", "Style.Triggers", "ControlTemplate", "ControlTemplate.Triggers",
        "DataTemplate", "ItemsPanelTemplate"
    };

    internal sealed record Result(int ViewCount, int TextSurfaceCount, int ControlStateCount);
    private sealed record Theme(
        string Name,
        string Directory,
        Dictionary<string, ColorValue> Brushes,
        Dictionary<string, XElement> KeyedStyles,
        Dictionary<string, XElement> ImplicitStyles,
        IReadOnlyList<ColorValue> CanvasColors);

    private readonly record struct ColorValue(double Red, double Green, double Blue, double Alpha, string Label)
    {
        internal ColorValue CompositeOver(ColorValue background, double opacity = 1)
        {
            var alpha = Math.Clamp(Alpha * opacity, 0, 1);
            return new ColorValue(
                Red * alpha + background.Red * (1 - alpha),
                Green * alpha + background.Green * (1 - alpha),
                Blue * alpha + background.Blue * (1 - alpha),
                1,
                Label);
        }
    }

    internal static Result Verify(string opticonRoot)
    {
        var failures = new List<string>();
        var textSurfaceCount = 0;
        var controlStateCount = 0;
        var viewCount = 0;

        foreach (var projectName in new[] { "Taildesk.Admin", "Taildesk.Setup" })
        {
            var directory = Path.Combine(opticonRoot, "src", projectName);
            var appPath = Path.Combine(directory, "App.xaml");
            var app = Load(appPath);
            var theme = BuildTheme(projectName, directory, app);

            foreach (var style in app.Descendants().Where(IsStyle))
                controlStateCount += AuditStyle(theme, style, $"{projectName}/App.xaml", failures);

            foreach (var path in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories)
                         .Where(path => !Path.GetFileName(path).Equals("App.xaml", StringComparison.OrdinalIgnoreCase)
                                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                viewCount++;
                var document = Load(path);
                foreach (var style in document.Descendants().Where(IsStyle))
                    controlStateCount += AuditStyle(theme, style, Relative(opticonRoot, path), failures);

                var root = document.Root ?? throw new InvalidOperationException($"Empty XAML file: {path}");
                var windowProperties = ResolveProperties(theme, theme.ImplicitStyles.GetValueOrDefault("Window"));
                var foreground = ResolveColor(theme, windowProperties.GetValueOrDefault("Foreground"));
                var background = ResolveColor(theme, windowProperties.GetValueOrDefault("Background"));
                AuditElement(theme, root, foreground, background, 1, Relative(opticonRoot, path), failures,
                    ref textSurfaceCount);
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"{failures.Count} text/background combination(s) are below {MinimumContrast:F1}:1:\n  "
                + string.Join("\n  ", failures));

        return new Result(viewCount, textSurfaceCount, controlStateCount);
    }

    private static Theme BuildTheme(string name, string directory, XDocument app)
    {
        var brushes = app.Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush" && element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(Xaml + "Key")!.Value,
                element => ParseColor(element.Attribute("Color")?.Value
                                      ?? throw new InvalidOperationException("A SolidColorBrush has no Color.")),
                StringComparer.Ordinal);
        var styles = app.Descendants().Where(IsStyle).ToList();
        var keyed = styles.Where(style => style.Attribute(Xaml + "Key") is not null)
            .ToDictionary(style => style.Attribute(Xaml + "Key")!.Value, style => style, StringComparer.Ordinal);
        var implicitStyles = styles.Where(style => style.Attribute(Xaml + "Key") is null)
            .ToDictionary(style => TargetType(style), style => style, StringComparer.Ordinal);
        var canvases = new[] { "WindowBrush", "RailBrush", "PanelBrush", "PanelAltBrush" }
            .Where(brushes.ContainsKey).Select(key => brushes[key]).ToList();
        return new Theme(name, directory, brushes, keyed, implicitStyles, canvases);
    }

    private static void AuditElement(
        Theme theme,
        XElement element,
        ColorValue? inheritedForeground,
        ColorValue? inheritedBackground,
        double inheritedOpacity,
        string file,
        List<string> failures,
        ref int count)
    {
        var name = element.Name.LocalName;
        if (MetadataElements.Contains(name) || name.EndsWith(".Resources", StringComparison.Ordinal)
                                            || name.EndsWith(".Style", StringComparison.Ordinal)
                                            || name.EndsWith(".Template", StringComparison.Ordinal))
            return;

        var style = ResolveElementStyle(theme, element);
        var properties = ResolveProperties(theme, style);
        foreach (var attributeName in new[] { "Foreground", "Background", "Opacity" })
        {
            if (element.Attribute(attributeName) is { } attribute)
                properties[attributeName] = attribute.Value;
        }

        var foreground = ResolveColor(theme, properties.GetValueOrDefault("Foreground")) ?? inheritedForeground;
        var ownBackground = ResolveColor(theme, properties.GetValueOrDefault("Background"));
        // A CheckBox's Background paints only its 16px indicator. Its text content
        // sits beside that indicator directly on the containing canvas.
        var background = name == "CheckBox" ? inheritedBackground : ownBackground ?? inheritedBackground;
        var opacity = inheritedOpacity * ParseOpacity(properties.GetValueOrDefault("Opacity"));

        if (DisplaysText(element))
        {
            count++;
            CheckPair(foreground, background, opacity, inheritedBackground, Describe(file, element), failures);
        }

        foreach (var child in element.Elements())
            AuditElement(theme, child, foreground, background, opacity, file, failures, ref count);
    }

    private static int AuditStyle(Theme theme, XElement style, string file, List<string> failures)
    {
        var targetType = TargetType(style);
        if (!TextElements.Contains(targetType)) return 0;

        var chain = StyleChain(theme, style).ToList();
        var baseProperties = ResolveProperties(chain);
        var triggerGroups = chain
            .SelectMany(item => item.Descendants().Where(element => element.Name.LocalName == "Trigger"))
            .Where(trigger => trigger.Attribute("Property") is not null && trigger.Attribute("Value") is not null)
            .GroupBy(trigger => $"{trigger.Attribute("Property")!.Value}={trigger.Attribute("Value")!.Value}")
            .Select(group => group.ToList())
            .ToList();

        var checkedStates = 0;
        var combinations = 1 << Math.Min(triggerGroups.Count, 12);
        for (var mask = 0; mask < combinations; mask++)
        {
            var selected = Enumerable.Range(0, triggerGroups.Count)
                .Where(index => (mask & (1 << index)) != 0).ToList();
            if (HasContradictoryConditions(selected.Select(index => triggerGroups[index][0]))) continue;

            var properties = new Dictionary<string, string>(baseProperties, StringComparer.Ordinal);
            foreach (var index in selected)
            foreach (var trigger in triggerGroups[index])
                ApplySetters(properties, trigger);

            var foreground = ResolveColor(theme, properties.GetValueOrDefault("Foreground"));
            var backgrounds = BackgroundCandidates(theme, targetType, properties);
            if (foreground is null || backgrounds.Count == 0) continue;

            var state = selected.Count == 0
                ? "default"
                : string.Join(" + ", selected.Select(index =>
                    triggerGroups[index][0].Attribute("Property")!.Value + "="
                    + triggerGroups[index][0].Attribute("Value")!.Value));
            var opacity = ParseOpacity(properties.GetValueOrDefault("Opacity"));
            foreach (var background in backgrounds)
                CheckPair(foreground, background, opacity, null,
                    $"{file} {StyleLabel(style, targetType)} [{state}]", failures);
            checkedStates++;
        }
        return checkedStates;
    }

    private static List<ColorValue> BackgroundCandidates(Theme theme, string targetType,
        IReadOnlyDictionary<string, string> properties)
    {
        if (targetType == "CheckBox")
            return theme.CanvasColors.ToList();
        if (ResolveColor(theme, properties.GetValueOrDefault("Background")) is { } own)
            return [own];

        if (targetType == "ListBoxItem")
            return BackgroundFromImplicitStyle(theme, "ListBox");
        if (targetType == "MenuItem")
            return BackgroundFromImplicitStyle(theme, "ContextMenu");
        if (targetType is "DataGridCell" or "DataGridRow" or "DataGridTextColumn")
        {
            var grid = ResolveProperties(theme, theme.ImplicitStyles.GetValueOrDefault("DataGrid"));
            return new[] { "Background", "RowBackground", "AlternatingRowBackground" }
                .Select(property => ResolveColor(theme, grid.GetValueOrDefault(property)))
                .Where(color => color is not null).Select(color => color!.Value).Distinct().ToList();
        }
        return [];
    }

    private static List<ColorValue> BackgroundFromImplicitStyle(Theme theme, string type)
    {
        var properties = ResolveProperties(theme, theme.ImplicitStyles.GetValueOrDefault(type));
        return ResolveColor(theme, properties.GetValueOrDefault("Background")) is { } color ? [color] : [];
    }

    private static void CheckPair(ColorValue? foreground, ColorValue? background, double opacity,
        ColorValue? backdrop, string label, List<string> failures)
    {
        if (foreground is null)
        {
            failures.Add($"{label}: foreground is not explicitly resolvable");
            return;
        }
        if (background is null)
        {
            failures.Add($"{label}: background is not explicitly resolvable");
            return;
        }

        var backdrops = opacity < 1
            ? backdrop is { } known ? new[] { known } : Array.Empty<ColorValue>()
            : Array.Empty<ColorValue>();
        if (opacity < 1 && backdrops.Length == 0)
        {
            // The same semi-transparent control can appear on any of the fixed app canvases.
            // Its weakest real placement is the safe value to enforce.
            backdrops = [ParseColor("#111316"), ParseColor("#171A1E"), ParseColor("#1C2025")];
        }

        double ratio;
        if (opacity < 1)
        {
            ratio = backdrops.Min(canvas => Contrast(
                foreground.Value.CompositeOver(canvas, opacity),
                background.Value.CompositeOver(canvas, opacity)));
        }
        else
        {
            var renderedBackground = background.Value.CompositeOver(ParseColor("#000000"));
            var renderedForeground = foreground.Value.CompositeOver(renderedBackground);
            ratio = Contrast(renderedForeground, renderedBackground);
        }

        if (ratio + 0.0001 < MinimumContrast)
            failures.Add($"{label}: {foreground.Value.Label} on {background.Value.Label} is {ratio:F2}:1");
    }

    private static XElement? ResolveElementStyle(Theme theme, XElement element)
    {
        if (element.Attribute("Style") is { } styleAttribute
            && ResourceKey(styleAttribute.Value) is { } key
            && theme.KeyedStyles.TryGetValue(key, out var keyed))
            return keyed;
        return theme.ImplicitStyles.GetValueOrDefault(element.Name.LocalName);
    }

    private static Dictionary<string, string> ResolveProperties(Theme theme, XElement? style) =>
        style is null ? new(StringComparer.Ordinal) : ResolveProperties(StyleChain(theme, style));

    private static Dictionary<string, string> ResolveProperties(IEnumerable<XElement> chain)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var style in chain) ApplySetters(properties, style);
        return properties;
    }

    private static IEnumerable<XElement> StyleChain(Theme theme, XElement style)
    {
        if (style.Attribute("BasedOn") is { } basedOn)
        {
            var key = ResourceKey(basedOn.Value);
            XElement? parent = null;
            if (key is not null) theme.KeyedStyles.TryGetValue(key, out parent);
            if (parent is null && TypeResource(basedOn.Value) is { } type)
                theme.ImplicitStyles.TryGetValue(type, out parent);
            if (parent is not null)
                foreach (var ancestor in StyleChain(theme, parent)) yield return ancestor;
        }
        yield return style;
    }

    private static void ApplySetters(IDictionary<string, string> properties, XElement owner)
    {
        foreach (var setter in owner.Elements().Where(element => element.Name.LocalName == "Setter"))
        {
            var property = setter.Attribute("Property")?.Value;
            var value = setter.Attribute("Value")?.Value;
            if (property is not null && value is not null
                && property is "Foreground" or "Background" or "Opacity" or "RowBackground" or "AlternatingRowBackground")
                properties[property] = value;
        }
    }

    private static ColorValue? ResolveColor(Theme theme, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            return null;
        if (ResourceKey(value) is { } key && theme.Brushes.TryGetValue(key, out var resource))
            return resource with { Label = key };
        if (value.StartsWith('#')) return ParseColor(value);
        return null;
    }

    private static ColorValue ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(character => $"{character}{character}"));
        var alpha = 255;
        if (hex.Length == 8)
        {
            alpha = int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            hex = hex[2..];
        }
        if (hex.Length != 6) throw new InvalidOperationException($"Unsupported color value: {value}");
        return new ColorValue(
            int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d,
            alpha / 255d,
            value);
    }

    private static double Contrast(ColorValue foreground, ColorValue background)
    {
        static double Luminance(ColorValue color)
        {
            static double Linear(double value) =>
                value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            return 0.2126 * Linear(color.Red) + 0.7152 * Linear(color.Green) + 0.0722 * Linear(color.Blue);
        }
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static bool DisplaysText(XElement element)
    {
        var name = element.Name.LocalName;
        if (!TextElements.Contains(name)) return false;
        if (name is "TextBlock" or "Run" or "TextBox" or "PasswordBox" or "ComboBox") return true;
        return element.Attributes().Any(attribute =>
            attribute.Name.LocalName is "Text" or "Content" or "Header" or "ItemsSource" or "Binding");
    }

    private static bool HasContradictoryConditions(IEnumerable<XElement> triggers) =>
        triggers.GroupBy(trigger => trigger.Attribute("Property")!.Value)
            .Any(group => group.Select(trigger => trigger.Attribute("Value")!.Value).Distinct().Count() > 1);

    private static string TargetType(XElement style) =>
        NormalizeType(style.Attribute("TargetType")?.Value ?? string.Empty);

    private static string NormalizeType(string value)
    {
        var normalized = value.Trim().TrimStart('{').TrimEnd('}');
        if (normalized.StartsWith("x:Type ", StringComparison.Ordinal)) normalized = normalized[7..];
        var colon = normalized.IndexOf(':');
        return colon >= 0 ? normalized[(colon + 1)..] : normalized;
    }

    private static string? TypeResource(string value)
    {
        var marker = "x:Type ";
        var start = value.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = value.IndexOf('}', start);
        return NormalizeType(end < 0 ? value[start..] : value[start..end]);
    }

    private static string? ResourceKey(string value)
    {
        const string marker = "{StaticResource ";
        if (!value.StartsWith(marker, StringComparison.Ordinal) || value.Contains("x:Type", StringComparison.Ordinal))
            return null;
        return value[marker.Length..].TrimEnd('}').Trim();
    }

    private static bool IsStyle(XElement element) => element.Name.LocalName == "Style";
    private static double ParseOpacity(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) ? opacity : 1;
    private static XDocument Load(string path) => XDocument.Load(path, LoadOptions.SetLineInfo);
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string StyleLabel(XElement style, string targetType) =>
        style.Attribute(Xaml + "Key") is { } key ? $"style {key.Value}" : $"{targetType} style";
    private static string Describe(string file, XElement element)
    {
        var line = (element as IXmlLineInfo)?.HasLineInfo() == true ? $":{((IXmlLineInfo)element).LineNumber}" : string.Empty;
        var text = element.Attribute("Text")?.Value ?? element.Attribute("Content")?.Value
                   ?? element.Attribute("Header")?.Value ?? element.Name.LocalName;
        if (text.Length > 48) text = text[..45] + "...";
        return $"{file}{line} <{element.Name.LocalName}> \"{text}\"";
    }
}
