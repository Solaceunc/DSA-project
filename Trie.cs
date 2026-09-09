namespace MiniSearchEngine;

/// <summary>
/// Prefix tree (Trie) implemented completely from scratch — no third-party
/// libraries, no built-in lookup tricks. Stores the whole indexed vocabulary
/// so that autocomplete can enumerate every stored word sharing a prefix.
///
///   Insert(word, frequency)  — O(L)          L = word length
///   Contains(word)           — O(L)
///   GetWordsWithPrefix(p)    — O(L + size of matching sub-trie)
///
/// The whole class is guarded by a single lock. Indexing rebuilds the Trie
/// while HTTP requests may be reading it, so this keeps every request thread
/// consistent at trivial cost for an academic project.
/// </summary>
public sealed class Trie
{
    private readonly TrieNode _root = new();
    private readonly object _lock = new();

    /// <summary>Number of distinct complete words currently stored.</summary>
    public int WordCount { get; private set; }

    /// <summary>
    /// Inserts (or merges) one word. <paramref name="frequency"/> is the corpus
    /// occurrence count of that word; repeated inserts for the same word add up.
    /// </summary>
    public void Insert(string word, int frequency = 1)
    {
        if (string.IsNullOrWhiteSpace(word))
            return;

        lock (_lock)
        {
            var node = _root;
            foreach (var ch in word)
            {
                if (!node.Children.TryGetValue(ch, out var next))
                {
                    next = new TrieNode();
                    node.Children[ch] = next;          // grow the tree edge by edge
                }
                node = next;
            }

            if (!node.IsEndOfWord)
            {
                node.IsEndOfWord = true;
                node.Word = word;
                WordCount++;
            }
            node.Frequency += frequency;               // total occurrences in the corpus
        }
    }

    /// <summary>True when the exact word has been inserted before.</summary>
    public bool Contains(string word)
    {
        if (string.IsNullOrEmpty(word))
            return false;

        lock (_lock)
        {
            var node = FindNode(word);
            return node is not null && node.IsEndOfWord;
        }
    }

    /// <summary>
    /// Walks the prefix, then collects every complete word in that sub-tree.
    /// Returns the top <paramref name="maxResults"/> completions ranked by
    /// corpus frequency (descending, then alphabetical for stable output).
    /// </summary>
    public List<(string Word, int Frequency)> GetWordsWithPrefix(string prefix, int maxResults)
    {
        var results = new List<(string Word, int Frequency)>();

        if (string.IsNullOrEmpty(prefix) || maxResults <= 0)
            return results;

        lock (_lock)
        {
            var subtreeRoot = FindNode(prefix);
            if (subtreeRoot is null)
                return results;                        // prefix not in the vocabulary

            // Depth-first traversal of the matching sub-trie.
            var stack = new Stack<TrieNode>();
            stack.Push(subtreeRoot);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.IsEndOfWord && node.Word is not null)
                    results.Add((node.Word, node.Frequency));

                // Push every child; they are popped back in arbitrary order here,
                // the final OrderBy/ThenBy below provides the deterministic ranking.
                foreach (var child in node.Children.Values)
                    stack.Push(child);
            }
        }

        return results
            .OrderByDescending(w => w.Frequency)       // most common words first
            .ThenBy(w => w.Word, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>Walks from the root along <paramref name="prefix"/>; null if the path dies.</summary>
    private TrieNode? FindNode(string prefix)
    {
        var node = _root;
        foreach (var ch in prefix)
        {
            if (!node.Children.TryGetValue(ch, out var next))
                return null;
            node = next;
        }
        return node;
    }
}
