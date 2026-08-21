using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// spawn_markdown — render a markdown document as an in-world RadiantUI panel. This is the
/// "leave a readable note for the user" tool: the agent writes markdown, the user gets a
/// grabbable, scrollable, styled window in front of them. Markdown is converted to the engine's
/// rich-text dialect (b/i/s/u/color/size + <noparse> escaping) and laid out as one UIX Text per
/// block inside a ScrollArea, so arbitrary-length documents stay readable.
/// </summary>
internal static class ToolsMarkdown
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("spawn_markdown",
            "Render markdown as an in-world RadiantUI panel (title bar, pin + close buttons, scrollable content) — " +
            "THE way to hand the user a readable document/report/note in-game. Supports headers, bold/italic/strike, " +
            "inline code + fenced code blocks, bullet/numbered lists, blockquotes, tables (best-effort), links " +
            "(shown, not clickable), horizontal rules; literal <angle brackets> are safe. Placement: default floats " +
            "it in front of the local user facing them ('inFrontOf' picks another user, 'distance' tunes how far); " +
            "or give explicit 'position' (+ optional 'lookAt'); or 'replaceId' to swap out a previously spawned " +
            "panel in place (same pose, old one destroyed). Panel is persistent + user-movable (grab the chrome); " +
            "close button destroys it. Returns the panel root RefID — keep it for replaceId updates.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"markdown\":{\"type\":\"string\",\"description\":\"The markdown source.\"}," +
            "\"markdownPath\":{\"type\":\"string\",\"description\":\"Read the markdown from this file instead (for long documents).\"}," +
            "\"title\":{\"type\":\"string\",\"default\":\"Note\",\"description\":\"Panel title-bar text.\"}," +
            "\"widthPx\":{\"type\":\"integer\",\"default\":800,\"description\":\"Canvas width in UIX px (~0.8 m at default scale).\"}," +
            "\"heightPx\":{\"type\":\"integer\",\"default\":1000,\"description\":\"Canvas height in UIX px; content beyond it scrolls.\"}," +
            "\"fontSize\":{\"type\":\"number\",\"default\":24,\"description\":\"Body font size in canvas px; headers scale from it.\"}," +
            "\"canvasScale\":{\"type\":\"number\",\"default\":0.001,\"description\":\"Meters per canvas px (0.001 -> an 800 px panel is 0.8 m wide).\"}," +
            "\"inFrontOf\":{\"type\":\"string\",\"description\":\"User name/id to place the panel in front of (default: local user).\"}," +
            "\"distance\":{\"type\":\"number\",\"default\":0.9,\"description\":\"Meters in front of the user.\"}," +
            "\"position\":{\"description\":\"Explicit world-space float3 — overrides inFrontOf.\"}," +
            "\"lookAt\":{\"description\":\"World-space float3 the panel should face (e.g. the viewer's head). Only with 'position'.\"}," +
            "\"parentId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"replaceId\":{\"type\":\"string\",\"description\":\"RefID of a panel previously spawned by this tool: destroy it and spawn the new one at its exact pose.\"}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}}}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string markdown = OptString(args, "markdown")
                    ?? (OptString(args, "markdownPath") is string path
                        ? File.ReadAllText(path)
                        : throw new ArgumentException("Provide 'markdown' (inline) or 'markdownPath' (file)"));
                string title = OptString(args, "title") ?? "Note";
                int widthPx = Math.Clamp(OptInt(args, "widthPx", 800), 200, 4000);
                int heightPx = Math.Clamp(OptInt(args, "heightPx", 1000), 150, 6000);
                float fontSize = Math.Clamp((float)(args["fontSize"]?.GetValue<double>() ?? 24.0), 6f, 128f);
                float canvasScale = Math.Clamp((float)(args["canvasScale"]?.GetValue<double>() ?? 0.001), 0.00005f, 0.1f);
                float distance = Math.Clamp((float)(args["distance"]?.GetValue<double>() ?? 0.9), 0.2f, 20f);
                string? inFrontOf = OptString(args, "inFrontOf");
                string parentId = OptString(args, "parentId") ?? "Root";
                string? replaceId = OptString(args, "replaceId");
                bool undoable = OptBool(args, "undoable", true);

                var blocks = MarkdownRichText.Convert(markdown);
                if (blocks.Count == 0)
                    throw new ArgumentException("Markdown converted to zero content blocks (empty input?)");

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "spawn_markdown", () =>
                {
                    // pose to inherit when replacing an earlier panel
                    float3? replacePos = null; floatQ? replaceRot = null; float3? replaceScale = null;
                    if (replaceId != null)
                    {
                        var oldElement = Resolve.Element(world, replaceId);
                        var oldSlot = oldElement as Slot ?? (oldElement as Component)?.Slot
                            ?? throw new ArgumentException($"replaceId {replaceId} is neither a slot nor a component");
                        replacePos = oldSlot.GlobalPosition;
                        replaceRot = oldSlot.GlobalRotation;
                        replaceScale = oldSlot.GlobalScale;
                        oldSlot.Destroy();
                    }

                    var parent = Resolve.Slot(world, parentId);
                    var root = parent.AddSlot(title, persistent: true);
                    root.Tag = "McpLinkMarkdownPanel";

                    var ui = RadiantUI_Panel.SetupPanel(root, title, new float2(widthPx, heightPx),
                        pinButton: true, closeButton: true);
                    RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: false);

                    ui.ScrollArea(Alignment.TopCenter);
                    ui.VerticalLayout(fontSize * 0.4f, fontSize * 0.75f,
                        Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
                    ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);

                    // each block is its own Text so headers/code/quotes can differ in size & color;
                    // UIX Text is an ILayoutElement, so the VerticalLayout stacks them by their
                    // own wrapped preferred height — no height math needed here
                    ui.Style.MinHeight = -1;
                    ui.Style.PreferredHeight = -1;
                    ui.Style.FlexibleHeight = -1;
                    ui.Style.TextAutoSizeMin = 0;
                    foreach (var block in blocks)
                    {
                        var text = ui.Text(block.Text, fontSize * block.Scale,
                            bestFit: false, alignment: Alignment.TopLeft, parseRTF: true);
                        text.HorizontalAlign.Value = Elements.Assets.TextHorizontalAlignment.Left;
                        text.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;
                        if (block.Color is colorX blockColor)
                            text.Color.Value = blockColor;
                    }

                    // placement — canvasScale multiplies in AFTER positioning: SetupPanel leaves the
                    // canvas at 1 px = 1 m (a "1 km panel" live-bug), and PositionInFrontOfUser's
                    // ScaleToUser would stomp a pre-set scale
                    if (args["position"] is JsonNode positionNode)
                    {
                        root.GlobalPosition = (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!;
                        if (args["lookAt"] is JsonNode lookAtNode)
                        {
                            var lookAt = (float3)Encode.Decode(lookAtNode.DeepClone(), typeof(float3), world)!;
                            // RadiantUI canvases read from their -Z side, so point +Z away from the viewer
                            var away = root.GlobalPosition - lookAt;
                            if (away.SqrMagnitude > 1e-8f)
                                root.GlobalRotation = floatQ.LookRotation(away.Normalized, float3.Up);
                        }
                        root.LocalScale *= canvasScale;
                    }
                    else if (replacePos is float3 pos)
                    {
                        root.GlobalPosition = pos;
                        root.GlobalRotation = replaceRot!.Value;
                        root.GlobalScale = replaceScale!.Value;
                    }
                    else
                    {
                        var user = ToolsInteract.FindUser(world, inFrontOf);
                        SlotPositioning.PositionInFrontOfUser(root, float3.Backward, null, distance, user,
                            scale: true, checkOcclusion: true, preserveUp: true);
                        root.LocalScale *= canvasScale;
                    }

                    if (undoable)
                        UndoUtil.RecordSpawn(root, "spawn_markdown");

                    return (JsonNode)new JsonObject
                    {
                        ["panel"] = Encode.ElementRef(root),
                        ["blocks"] = blocks.Count,
                        ["title"] = title,
                        ["hint"] = "Pass this RefID as replaceId to update the panel in place. The user can grab the window chrome to move it; its close button destroys it.",
                    };
                }));
            }));
    }
}

/// <summary>
/// Markdown → Resonite rich-text converter. Line-based, dependency-free, tuned for agent-written
/// notes: paragraph line breaks are preserved (not soft-wrapped away), tables render best-effort
/// with │ separators, links show their URL dimmed (world text isn't clickable). Literal '<' in
/// source text survives via &lt;noparse&gt; so user content can't break the tag parser.
/// </summary>
internal static class MarkdownRichText
{
    internal sealed record Block(string Text, float Scale, colorX? Color);

    private const string CodeColor = "#A8E5A0";
    private const string LinkColor = "#7FB2FF";
    private const string QuoteColor = "#B8B8C8";
    private const string RuleColor = "#666677";
    private const char EscapedLt = ''; // private-use placeholder so literal '<' survives inline passes

    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RulePattern = new(@"^\s*([-*_])\s*(\1\s*){2,}$", RegexOptions.Compiled);
    private static readonly Regex ListPattern = new(@"^(\s*)([-*+]|\d{1,3}\.)\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorPattern = new(@"^\s*\|?[\s:|-]+\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex CodeSpanPattern = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex BoldPattern = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex ItalicPattern = new(@"\*([^*\s](?:[^*]*[^*\s])?)\*|\b_([^_\s](?:[^_]*[^_\s])?)_\b", RegexOptions.Compiled);
    private static readonly Regex StrikePattern = new(@"~~(.+?)~~", RegexOptions.Compiled);
    private static readonly Regex ImagePattern = new(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    public static List<Block> Convert(string markdown)
    {
        var blocks = new List<Block>();
        var paragraph = new StringBuilder();
        var codeBlock = new StringBuilder();
        bool inCodeFence = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
                return;
            blocks.Add(new Block(paragraph.ToString(), 1f, null));
            paragraph.Clear();
        }

        void FlushCode()
        {
            if (codeBlock.Length == 0)
                return;
            blocks.Add(new Block($"<color={CodeColor}>{codeBlock}</color>", 0.85f, null));
            codeBlock.Clear();
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeFence) FlushCode();
                else FlushParagraph();
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                if (codeBlock.Length > 0)
                    codeBlock.Append('\n');
                codeBlock.Append("  ").Append(EscapeFinal(Escape(rawLine.TrimEnd())));
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (HeadingPattern.Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                float scale = heading.Groups[1].Value.Length switch { 1 => 1.65f, 2 => 1.35f, 3 => 1.15f, _ => 1.05f };
                blocks.Add(new Block($"<b>{Inline(heading.Groups[2].Value)}</b>", scale, null));
                continue;
            }

            if (RulePattern.IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new Block($"<color={RuleColor}>{new string('─', 40)}</color>", 0.85f, null));
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                string quoted = line.TrimStart().TrimStart('>').TrimStart();
                AppendLine(paragraph, $"<color={QuoteColor}>▎ <i>{Inline(quoted)}</i></color>");
                continue;
            }

            if (ListPattern.Match(line) is { Success: true } item)
            {
                int level = Math.Min(item.Groups[1].Value.Replace("\t", "  ").Length / 2, 2);
                string marker = item.Groups[2].Value;
                if (marker is "-" or "*" or "+")
                    marker = level switch { 0 => "•", 1 => "◦", _ => "▪" };
                AppendLine(paragraph, $"{new string(' ', level * 4)}{marker} {Inline(item.Groups[3].Value)}");
                continue;
            }

            if (line.Contains('|') && line.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                if (TableSeparatorPattern.IsMatch(line))
                    continue;
                var cells = line.Trim().Trim('|').Split('|').Select(c => Inline(c.Trim()));
                AppendLine(paragraph, $"<color={CodeColor}>│</color> {string.Join($" <color={CodeColor}>│</color> ", cells)}");
                continue;
            }

            AppendLine(paragraph, Inline(line));
        }

        if (inCodeFence) FlushCode();
        FlushParagraph();
        return blocks;
    }

    private static void AppendLine(StringBuilder paragraph, string line)
    {
        if (paragraph.Length > 0)
            paragraph.Append('\n');
        paragraph.Append(line);
    }

    /// <summary>Inline markdown → rich text for one line (input is raw markdown text).</summary>
    private static string Inline(string text)
    {
        text = Escape(text);
        text = CodeSpanPattern.Replace(text, m => $"<color={CodeColor}>{m.Groups[1].Value}</color>");
        text = ImagePattern.Replace(text, m => $"🖼 {m.Groups[1].Value} <color={LinkColor}><u>{m.Groups[2].Value}</u></color>");
        text = LinkPattern.Replace(text, m => $"{m.Groups[1].Value} <color={LinkColor}><u>{m.Groups[2].Value}</u></color>");
        text = BoldPattern.Replace(text, m => $"<b>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</b>");
        text = ItalicPattern.Replace(text, m => $"<i>{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}</i>");
        text = StrikePattern.Replace(text, m => $"<s>{m.Groups[1].Value}</s>");
        return EscapeFinal(text);
    }

    private static string Escape(string text) => text.Replace('<', EscapedLt);

    /// <summary>Literal '&lt;' renders via a noparse wrapper so it can't open a tag.</summary>
    private static string EscapeFinal(string text) =>
        text.Replace(EscapedLt.ToString(), "<noparse><</noparse>");
}
