// =============================================================================
// Diff Tests — Virtual DOM Diffing Algorithm
// =============================================================================
// Tests the pure Operations.Diff() function that computes minimal patches
// to transform one virtual DOM tree into another. No browser dependencies.
//
// Coverage:
//   • Initial render (null → node)
//   • Element replacement (tag change)
//   • Attribute diffing (add, remove, update, same-order fast path)
//   • Handler diffing (add, remove, update)
//   • Children diffing (add, remove, clear, reorder)
//   • Text node diffing
//   • Raw HTML diffing
//   • Memo/LazyMemo key comparison
//   • LIS algorithm correctness
//   • Head/tail skip optimization
//   • SetChildrenHtml fast path (0→N children)
//   • Complete replacement fast path
// =============================================================================

using Abies.DOM;
using Attribute = Abies.DOM.Attribute;

namespace Abies.Tests;

public class DiffTests
{
    // =========================================================================
    // Initial Render
    // =========================================================================

    [Fact]
    public void Diff_NullOldNode_EmitsAddRoot()
    {
        var node = new Element("e1", "div", []);

        var patches = Operations.Diff(null, node);

        var addRoot = Assert.Single(patches);
        var root = Assert.IsType<AddRoot>(addRoot);
        Assert.Equal("div", root.Element.Tag);
    }

    // =========================================================================
    // Identical Nodes — No Patches
    // =========================================================================

    [Fact]
    public void Diff_IdenticalElements_NoPatch()
    {
        var node = new Element("e1", "div", [new Attribute("a1", "class", "x")]);

        var patches = Operations.Diff(node, node);

        Assert.Empty(patches);
    }

    [Fact]
    public void Diff_EqualButDifferentInstances_NoPatch()
    {
        var old = new Element("e1", "div", [new Attribute("a1", "class", "x")]);
        var @new = new Element("e1", "div", [new Attribute("a1", "class", "x")]);

        var patches = Operations.Diff(old, @new);

        Assert.Empty(patches);
    }

    // =========================================================================
    // Element Replacement
    // =========================================================================

    [Fact]
    public void Diff_DifferentTags_EmitsAddRoot()
    {
        var old = new Element("e1", "div", []);
        var @new = new Element("e1", "span", []);

        var patches = Operations.Diff(old, @new);

        var addRoot = Assert.Single(patches);
        Assert.IsType<AddRoot>(addRoot);
    }

    [Fact]
    public void Diff_DifferentTagsNested_EmitsReplaceChild()
    {
        var oldChild = new Element("c1", "div", []);
        var newChild = new Element("c1", "span", []);
        var oldParent = new Element("p1", "section", [], oldChild);
        var newParent = new Element("p1", "section", [], newChild);

        var patches = Operations.Diff(oldParent, newParent);

        Assert.Contains(patches, p => p is ReplaceChild);
    }

    // =========================================================================
    // Attribute Diffing
    // =========================================================================

    [Fact]
    public void Diff_AddAttribute_EmitsAddAttribute()
    {
        var old = new Element("e1", "div", []);
        var @new = new Element("e1", "div", [new Attribute("a1", "class", "active")]);

        var patches = Operations.Diff(old, @new);

        var add = Assert.Single(patches);
        var addAttr = Assert.IsType<AddAttribute>(add);
        Assert.Equal("class", addAttr.Attribute.Name);
        Assert.Equal("active", addAttr.Attribute.Value);
    }

    [Fact]
    public void Diff_RemoveAttribute_EmitsRemoveAttribute()
    {
        var old = new Element("e1", "div", [new Attribute("a1", "class", "active")]);
        var @new = new Element("e1", "div", []);

        var patches = Operations.Diff(old, @new);

        var remove = Assert.Single(patches);
        Assert.IsType<RemoveAttribute>(remove);
    }

    [Fact]
    public void Diff_UpdateAttributeValue_EmitsUpdateAttribute()
    {
        var old = new Element("e1", "div", [new Attribute("a1", "class", "old")]);
        var @new = new Element("e1", "div", [new Attribute("a1", "class", "new")]);

        var patches = Operations.Diff(old, @new);

        var update = Assert.Single(patches);
        var updateAttr = Assert.IsType<UpdateAttribute>(update);
        Assert.Equal("new", updateAttr.Value);
    }

    [Fact]
    public void Diff_SameAttributes_NoPatch()
    {
        var old = new Element("e1", "div",
        [
            new Attribute("a1", "class", "x"),
            new Attribute("a2", "title", "y")
        ]);
        var @new = new Element("e1", "div",
        [
            new Attribute("a1", "class", "x"),
            new Attribute("a2", "title", "y")
        ]);

        var patches = Operations.Diff(old, @new);

        Assert.Empty(patches);
    }

    // =========================================================================
    // Handler Diffing
    // =========================================================================

    [Fact]
    public void Diff_AddHandler_EmitsAddHandler()
    {
        var old = new Element("e1", "button", []);
        var handler = new Handler("click", "cmd-1", null, "h1");
        var @new = new Element("e1", "button", [handler]);

        var patches = Operations.Diff(old, @new);

        var add = Assert.Single(patches);
        var addHandler = Assert.IsType<AddHandler>(add);
        Assert.Equal("click", addHandler.Handler.EventName);
        Assert.Equal("cmd-1", addHandler.Handler.CommandId);
    }

    [Fact]
    public void Diff_RemoveHandler_EmitsRemoveHandler()
    {
        var handler = new Handler("click", "cmd-1", null, "h1");
        var old = new Element("e1", "button", [handler]);
        var @new = new Element("e1", "button", []);

        var patches = Operations.Diff(old, @new);

        var remove = Assert.Single(patches);
        Assert.IsType<RemoveHandler>(remove);
    }

    [Fact]
    public void Diff_UpdateHandler_EmitsUpdateHandler()
    {
        var oldHandler = new Handler("click", "cmd-1", null, "h1");
        var newHandler = new Handler("click", "cmd-2", null, "h1");
        var old = new Element("e1", "button", [oldHandler]);
        var @new = new Element("e1", "button", [newHandler]);

        var patches = Operations.Diff(old, @new);

        var update = Assert.Single(patches);
        var updateHandler = Assert.IsType<UpdateHandler>(update);
        Assert.Equal("cmd-1", updateHandler.OldHandler.CommandId);
        Assert.Equal("cmd-2", updateHandler.NewHandler.CommandId);
    }

    [Fact]
    public void Diff_HandlerNameIsFullAttributeName()
    {
        // Regression test: Handler.Name must return "data-event-click"
        // (the Attribute.Name), not "click" (the EventName).
        // This caused the double-prefix bug.
        var handler = new Handler("click", "cmd-1", null, "h1");
        var old = new Element("e1", "button", [handler]);

        var newHandler = new Handler("click", "cmd-2", null, "h1");
        var @new = new Element("e1", "button", [newHandler]);

        var patches = Operations.Diff(old, @new);

        var update = Assert.IsType<UpdateHandler>(Assert.Single(patches));
        // Both old and new handler .Name should be the full attribute name
        Assert.Equal("data-event-click", update.OldHandler.Name);
        Assert.Equal("data-event-click", update.NewHandler.Name);
    }

    // =========================================================================
    // Children Diffing — Basic
    // =========================================================================

    [Fact]
    public void Diff_AddChild_EmitsSetChildrenHtml()
    {
        // Going from 0 → N children uses the SetChildrenHtml fast path.
        var old = new Element("e1", "div", []);
        var @new = new Element("e1", "div", [], new Element("c1", "span", []));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is SetChildrenHtml);
    }

    [Fact]
    public void Diff_RemoveAllChildren_EmitsClearChildren()
    {
        var old = new Element("e1", "div", [],
            new Element("c1", "span", []),
            new Element("c2", "span", []));
        var @new = new Element("e1", "div", []);

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is ClearChildren);
    }

    [Fact]
    public void Diff_AppendChild_EmitsAddChild()
    {
        // Head skip matches c1, then c2 is new → AddChild.
        var old = new Element("e1", "div", [],
            new Element("c1", "span", []));
        var @new = new Element("e1", "div", [],
            new Element("c1", "span", []),
            new Element("c2", "span", []));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is AddChild);
    }

    [Fact]
    public void Diff_RemoveChild_EmitsRemoveChild()
    {
        // Head skip matches c1, then c2 is removed.
        var old = new Element("e1", "div", [],
            new Element("c1", "span", []),
            new Element("c2", "span", []));
        var @new = new Element("e1", "div", [],
            new Element("c1", "span", []));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is RemoveChild);
    }

    // =========================================================================
    // Children Diffing — Keyed Reorder (LIS)
    // =========================================================================

    [Fact]
    public void Diff_SwapTwoChildren_EmitsMoveChild()
    {
        var old = new Element("e1", "ul", [],
            new Element("c1", "li", [], new Text("t1", "A")),
            new Element("c2", "li", [], new Text("t2", "B")));
        var @new = new Element("e1", "ul", [],
            new Element("c2", "li", [], new Text("t2", "B")),
            new Element("c1", "li", [], new Text("t1", "A")));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is MoveChild);
    }

    [Fact]
    public void Diff_SwapTwoInThousand_MinimalMoves()
    {
        // Swap elements at positions 1 and 998 (like js-framework-benchmark).
        // LIS should identify 998 elements as in-order, producing only 2 moves.
        var oldChildren = Enumerable.Range(0, 1000)
            .Select(i => (Node)new Element($"c{i}", "li", [], new Text($"t{i}", $"Item {i}")))
            .ToArray();

        var newOrder = (int[])[0, 998, .. Enumerable.Range(2, 996), 1, 999];
        var newChildren = newOrder
            .Select(i => (Node)new Element($"c{i}", "li", [], new Text($"t{i}", $"Item {i}")))
            .ToArray();

        var old = new Element("e1", "ul", [], oldChildren);
        var @new = new Element("e1", "ul", [], newChildren);

        var patches = Operations.Diff(old, @new);

        var moveCount = patches.Count(p => p is MoveChild);
        Assert.Equal(2, moveCount);
    }

    [Fact]
    public void Diff_ReverseOrder_EmitsMoves()
    {
        var old = new Element("e1", "ul", [],
            new Element("c1", "li", []),
            new Element("c2", "li", []),
            new Element("c3", "li", []));
        var @new = new Element("e1", "ul", [],
            new Element("c3", "li", []),
            new Element("c2", "li", []),
            new Element("c1", "li", []));

        var patches = Operations.Diff(old, @new);

        // All elements are reused (no add/remove), only moves.
        Assert.DoesNotContain(patches, p => p is AddChild);
        Assert.DoesNotContain(patches, p => p is RemoveChild);
        Assert.Contains(patches, p => p is MoveChild);
    }

    // =========================================================================
    // Children Diffing — Complete Replacement Fast Path
    // =========================================================================

    [Fact]
    public void Diff_AllDifferentKeys_EmitsClearAndSetChildrenHtml()
    {
        // Must exceed SmallChildCountThreshold (8) to hit the keyed
        // reconciliation path that has the complete replacement fast path.
        var oldChildren = Enumerable.Range(0, 10)
            .Select(i => (Node)new Element($"a{i}", "li", []))
            .ToArray();
        var newChildren = Enumerable.Range(0, 10)
            .Select(i => (Node)new Element($"b{i}", "li", []))
            .ToArray();

        var old = new Element("e1", "ul", [], oldChildren);
        var @new = new Element("e1", "ul", [], newChildren);

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is ClearChildren);
        Assert.Contains(patches, p => p is SetChildrenHtml);
    }

    // =========================================================================
    // Children Diffing — Head/Tail Skip
    // =========================================================================

    [Fact]
    public void Diff_AppendToEnd_HeadSkipMatchesExisting()
    {
        // All old children match new head → only additions at the end.
        var old = new Element("e1", "div", [],
            new Element("c1", "p", []),
            new Element("c2", "p", []));
        var @new = new Element("e1", "div", [],
            new Element("c1", "p", []),
            new Element("c2", "p", []),
            new Element("c3", "p", []));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is AddChild);
        Assert.DoesNotContain(patches, p => p is RemoveChild);
        Assert.DoesNotContain(patches, p => p is MoveChild);
    }

    [Fact]
    public void Diff_RemoveFromEnd_HeadSkipMatchesRemaining()
    {
        var old = new Element("e1", "div", [],
            new Element("c1", "p", []),
            new Element("c2", "p", []),
            new Element("c3", "p", []));
        var @new = new Element("e1", "div", [],
            new Element("c1", "p", []),
            new Element("c2", "p", []));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is RemoveChild);
        Assert.DoesNotContain(patches, p => p is AddChild);
    }

    // =========================================================================
    // Text Node Diffing
    // =========================================================================

    [Fact]
    public void Diff_UpdateText_EmitsUpdateText()
    {
        var old = new Element("e1", "p", [], new Text("t1", "old text"));
        var @new = new Element("e1", "p", [], new Text("t1", "new text"));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is UpdateText);
    }

    [Fact]
    public void Diff_SameText_NoPatch()
    {
        var old = new Element("e1", "p", [], new Text("t1", "same"));
        var @new = new Element("e1", "p", [], new Text("t1", "same"));

        var patches = Operations.Diff(old, @new);

        Assert.Empty(patches);
    }

    // =========================================================================
    // Raw HTML Diffing
    // =========================================================================

    [Fact]
    public void Diff_UpdateRawHtml_EmitsUpdateRaw()
    {
        var old = new Element("e1", "div", [], new RawHtml("r1", "<b>old</b>"));
        var @new = new Element("e1", "div", [], new RawHtml("r1", "<b>new</b>"));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is UpdateRaw);
    }

    // =========================================================================
    // Memo Node Diffing
    // =========================================================================

    [Fact]
    public void Diff_MemoSameKey_SkipsDiff()
    {
        Operations.ResetMemoCounters();

        var inner = new Element("e1", "div", [], new Text("t1", "content"));
        var old = new Memo<int>("m1", 42, inner);
        var @new = new Memo<int>("m1", 42, inner);

        var patches = Operations.Diff(old, @new);

        Assert.Empty(patches);
        Assert.Equal(1, Operations.MemoHits);
        Assert.Equal(0, Operations.MemoMisses);
    }

    [Fact]
    public void Diff_MemoDifferentKey_DiffsContent()
    {
        Operations.ResetMemoCounters();

        var oldInner = new Element("e1", "div", [], new Text("t1", "old"));
        var newInner = new Element("e1", "div", [], new Text("t1", "new"));
        var old = new Memo<int>("m1", 42, oldInner);
        var @new = new Memo<int>("m1", 43, newInner);

        var patches = Operations.Diff(old, @new);

        Assert.NotEmpty(patches);
        Assert.Equal(0, Operations.MemoHits);
        Assert.Equal(1, Operations.MemoMisses);
    }

    [Fact]
    public void Diff_LazyMemoSameKey_SkipsEvaluation()
    {
        Operations.ResetMemoCounters();
        var evaluationCount = 0;

        var inner = new Element("e1", "div", [], new Text("t1", "lazy"));
        var old = new LazyMemo<string>("l1", "key", () =>
        {
            evaluationCount++;
            return inner;
        }, inner); // CachedNode is pre-populated
        var @new = new LazyMemo<string>("l1", "key", () =>
        {
            evaluationCount++;
            return inner;
        });

        var patches = Operations.Diff(old, @new);

        Assert.Empty(patches);
        Assert.Equal(1, Operations.MemoHits);
        Assert.Equal(0, evaluationCount); // Factory was never called
    }

    [Fact]
    public void Diff_LazyMemoDifferentKey_EvaluatesAndDiffs()
    {
        Operations.ResetMemoCounters();

        var oldInner = new Element("e1", "div", [], new Text("t1", "v1"));
        var newInner = new Element("e1", "div", [], new Text("t1", "v2"));
        var old = new LazyMemo<string>("l1", "key1", () => oldInner, oldInner);
        var @new = new LazyMemo<string>("l1", "key2", () => newInner);

        var patches = Operations.Diff(old, @new);

        Assert.NotEmpty(patches);
        Assert.Equal(0, Operations.MemoHits);
        Assert.Equal(1, Operations.MemoMisses);
    }

    [Fact]
    public void Diff_MemoWithTupleKey_ValueEquality()
    {
        // Tuple keys use value equality — same values = same key.
        Operations.ResetMemoCounters();

        var inner = new Element("e1", "div", []);
        var old = new Memo<(int, bool)>("m1", (1, true), inner);
        var @new = new Memo<(int, bool)>("m1", (1, true), inner);

        var patches = Operations.Diff(old, @new);

        Assert.Empty(patches);
        Assert.Equal(1, Operations.MemoHits);
    }

    // =========================================================================
    // Void Element Diffing — Skip DiffChildren
    // =========================================================================

    [Fact]
    public void Diff_VoidElement_SkipsChildDiff()
    {
        // Void elements can't have children — diff should only process attributes.
        var old = new Element("e1", "input", [new Attribute("a1", "type", "text")]);
        var @new = new Element("e1", "input", [new Attribute("a1", "type", "password")]);

        var patches = Operations.Diff(old, @new);

        var update = Assert.Single(patches);
        Assert.IsType<UpdateAttribute>(update);
    }

    // =========================================================================
    // Mixed Attribute and Handler Operations
    // =========================================================================

    [Fact]
    public void Diff_MixedAttributeAndHandlerChanges_EmitsCorrectPatches()
    {
        var oldHandler = new Handler("click", "cmd-1", null, "h1");
        var old = new Element("e1", "button",
        [
            new Attribute("a1", "class", "btn"),
            oldHandler
        ],
            new Text("t1", "Click"));

        var newHandler = new Handler("click", "cmd-2", null, "h1");
        var @new = new Element("e1", "button",
        [
            new Attribute("a1", "class", "btn-primary"),
            newHandler
        ],
            new Text("t1", "Click"));

        var patches = Operations.Diff(old, @new);

        Assert.Contains(patches, p => p is UpdateAttribute);
        Assert.Contains(patches, p => p is UpdateHandler);
        Assert.Equal(2, patches.Count);
    }

    // =========================================================================
    // EventAttributeNames Cache
    // =========================================================================

    [Theory]
    [InlineData("click", "data-event-click")]
    [InlineData("input", "data-event-input")]
    [InlineData("submit", "data-event-submit")]
    [InlineData("keydown", "data-event-keydown")]
    public void EventAttributeNames_CachedEvent_ReturnsCorrectName(string eventName, string expected)
    {
        Assert.Equal(expected, EventAttributeNames.Get(eventName));
    }

    [Fact]
    public void EventAttributeNames_UnknownEvent_FallsBackToInterpolation()
    {
        Assert.Equal("data-event-custom", EventAttributeNames.Get("custom"));
    }
}
