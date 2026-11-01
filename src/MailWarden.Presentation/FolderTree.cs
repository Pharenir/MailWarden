using MailWarden.Core.Model;
using MailWarden.Core.Text;

namespace MailWarden.Presentation;

/// <summary>
/// A node of the folder tree of one account.
/// </summary>
/// <param name="Name">Last name segment, cleaned for display.</param>
/// <param name="Folder">The folder; null for a parent that does not exist as a folder (not selectable).</param>
/// <param name="Children">Child nodes, ordered with OrdinalIgnoreCase.</param>
public sealed record FolderTreeNode(string Name, MailFolder? Folder, IReadOnlyList<FolderTreeNode> Children);

/// <summary>
/// Builds the folder tree from the flat folder list.
/// </summary>
public static class FolderTree
{
    // Folder names come from the server: anything deeper stays together in the last node, so that later recursive walks
    // over the tree stay safe.
    private const int MaxDepth = 32;

    /// <summary>
    /// Splits <see cref="MailFolder.FullName"/> at '/'. Missing parents are nodes with Folder null (not selectable). Names
    /// are cleaned for display. Siblings are ordered with OrdinalIgnoreCase.
    /// </summary>
    /// <param name="folders">Folders of one account, in any order.</param>
    /// <returns>The top-level nodes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="folders"/> is null.</exception>
    public static IReadOnlyList<FolderTreeNode> Build(IEnumerable<MailFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var root = new NodeBuilder(string.Empty);
        foreach (MailFolder folder in folders)
        {
            NodeBuilder node = root;
            string[] segments = folder.FullName.Split('/', MaxDepth, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments.Length == 0 ? [folder.FullName] : segments)
            {
                node = node.GetOrAddChild(segment);
            }

            node.Folder = folder;
        }

        return root.BuildChildren();
    }

    /// <summary>Mutable node while the tree is built; children are found by their raw name segment.</summary>
    private sealed class NodeBuilder(string rawName)
    {
        private readonly Dictionary<string, NodeBuilder> children = new(StringComparer.Ordinal);

        public MailFolder? Folder { get; set; }

        public NodeBuilder GetOrAddChild(string segment)
        {
            if (!children.TryGetValue(segment, out NodeBuilder? child))
            {
                child = new NodeBuilder(segment);
                children.Add(segment, child);
            }

            return child;
        }

        public IReadOnlyList<FolderTreeNode> BuildChildren()
        {
            return children.Values
                .Select(child => child.Build())
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, StringComparer.Ordinal)
                .ToList();
        }

        private FolderTreeNode Build() => new(SafeDisplayText.Clean(rawName), Folder, BuildChildren());
    }
}
