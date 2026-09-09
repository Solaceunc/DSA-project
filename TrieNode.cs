namespace MiniSearchEngine;

/// <summary>
/// A single node of the prefix tree (Trie).
///
/// Every node owns:
///   • <see cref="Children"/> — a hash table (Dictionary) that maps one
///     character to the next node. Using a Dictionary instead of a fixed
///     [26] array keeps memory small for natural text and supports any
///     Unicode letter/digit the tokenizer emits.
///   • <see cref="IsEndOfWord"/> — true when the path from the root to this
///     node spells a complete word that was inserted into the Trie.
///   • <see cref="Frequency"/> — how many times that complete word occurs in
///     the indexed corpus; used to rank autocomplete suggestions.
/// </summary>
public sealed class TrieNode
{
    /// <summary>Next hop per character (hash table of edges leaving this node).</summary>
    public Dictionary<char, TrieNode> Children { get; } = new();

    /// <summary>True when the path down to this node spells a complete stored word.</summary>
    public bool IsEndOfWord { get; internal set; }

    /// <summary>Total corpus occurrences of the word ending here (0 when not an end-of-word).</summary>
    public int Frequency { get; internal set; }

    /// <summary>The full word spelled by the root→node path (filled for end-of-word nodes).</summary>
    public string? Word { get; internal set; }
}
