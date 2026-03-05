// =============================================================================
// Patch Instructions (ID-Addressed)
// =============================================================================
// A patch is a pure data description of a DOM mutation. The diff algorithm
// produces patches; a platform-specific host applies them (via JS interop,
// SSR string rendering, or a test renderer).
//
// All patches address DOM nodes by their stable string Id rather than
// positional paths. This enables O(1) element lookup via getElementById
// and makes patches order-independent and robust to concurrent mutations.
//
// Patch types use readonly struct for zero-allocation on the hot path.
// They implement the Patch marker interface for polymorphic dispatch.
//
// Patch type catalogue:
//   Tree mutations:     AddRoot, ReplaceChild, AddChild, RemoveChild, ClearChildren, SetChildrenHtml, MoveChild
//   Attribute mutations: UpdateAttribute, AddAttribute, RemoveAttribute
//   Handler mutations:   AddHandler, RemoveHandler, UpdateHandler
//   Text mutations:      UpdateText, AddText, RemoveText
//   Raw HTML mutations:  AddRaw, RemoveRaw, ReplaceRaw, UpdateRaw
// =============================================================================

using Abies.DOM;

namespace Abies;

/// <summary>
/// Marker interface for all patch operations.
/// </summary>
public interface Patch;

/// <summary>Set the root element (initial render).</summary>
public readonly struct AddRoot(Element element) : Patch
{
    public readonly Element Element = element;
}

/// <summary>Replace an element with another.</summary>
public readonly struct ReplaceChild(Element oldElement, Element newElement) : Patch
{
    public readonly Element OldElement = oldElement;
    public readonly Element NewElement = newElement;
}

/// <summary>Append a child element to a parent.</summary>
public readonly struct AddChild(Element parent, Element child) : Patch
{
    public readonly Element Parent = parent;
    public readonly Element Child = child;
}

/// <summary>Remove a child element from a parent.</summary>
public readonly struct RemoveChild(Element parent, Element child) : Patch
{
    public readonly Element Parent = parent;
    public readonly Element Child = child;
}

/// <summary>
/// Remove all children from an element.
/// More efficient than multiple RemoveChild operations.
/// </summary>
public readonly struct ClearChildren(Element parent, Node[] oldChildren) : Patch
{
    public readonly Element Parent = parent;
    public readonly Node[] OldChildren = oldChildren;
}

/// <summary>
/// Set all children via a single innerHTML assignment.
/// Dramatically faster than N individual AddChild patches — eliminates
/// N parseHtmlFragment + appendChild calls in the browser.
/// Used for the 0→N children fast path (initial render of a list).
/// </summary>
public readonly struct SetChildrenHtml(Element parent, Node[] children) : Patch
{
    public readonly Element Parent = parent;
    public readonly Node[] Children = children;
}

/// <summary>
/// Move a child element to a new position within its parent.
/// Uses insertBefore semantics: insert before the element with BeforeId, or append if null.
/// </summary>
public readonly struct MoveChild(Element parent, Element child, string? beforeId) : Patch
{
    public readonly Element Parent = parent;
    public readonly Element Child = child;
    public readonly string? BeforeId = beforeId;
}

/// <summary>Update an existing attribute's value.</summary>
public readonly struct UpdateAttribute(Element element, DOM.Attribute attribute, string value) : Patch
{
    public readonly Element Element = element;
    public readonly DOM.Attribute Attribute = attribute;
    public readonly string Value = value;
}

/// <summary>Add a new attribute to an element.</summary>
public readonly struct AddAttribute(Element element, DOM.Attribute attribute) : Patch
{
    public readonly Element Element = element;
    public readonly DOM.Attribute Attribute = attribute;
}

/// <summary>Remove an attribute from an element.</summary>
public readonly struct RemoveAttribute(Element element, DOM.Attribute attribute) : Patch
{
    public readonly Element Element = element;
    public readonly DOM.Attribute Attribute = attribute;
}

/// <summary>Add a new event handler to an element.</summary>
public readonly struct AddHandler(Element element, Handler handler) : Patch
{
    public readonly Element Element = element;
    public readonly Handler Handler = handler;
}

/// <summary>Remove an event handler from an element.</summary>
public readonly struct RemoveHandler(Element element, Handler handler) : Patch
{
    public readonly Element Element = element;
    public readonly Handler Handler = handler;
}

/// <summary>Update an event handler on an element (replace old handler with new one).</summary>
public readonly struct UpdateHandler(Element element, Handler oldHandler, Handler newHandler) : Patch
{
    public readonly Element Element = element;
    public readonly Handler OldHandler = oldHandler;
    public readonly Handler NewHandler = newHandler;
}

/// <summary>Update the text content of a text node.</summary>
public readonly struct UpdateText(Element parent, string text, string newId) : Patch
{
    public readonly Element Parent = parent;
    public readonly string Text = text;
    public readonly string NewId = newId;
}

/// <summary>Add a text node to a parent element.</summary>
public readonly struct AddText(Element parent, Text child) : Patch
{
    public readonly Element Parent = parent;
    public readonly Text Child = child;
}

/// <summary>Remove a text node from a parent element.</summary>
public readonly struct RemoveText(Element parent, Text child) : Patch
{
    public readonly Element Parent = parent;
    public readonly Text Child = child;
}

/// <summary>Add raw HTML to a parent element.</summary>
public readonly struct AddRaw(Element parent, RawHtml child) : Patch
{
    public readonly Element Parent = parent;
    public readonly RawHtml Child = child;
}

/// <summary>Remove raw HTML from a parent element.</summary>
public readonly struct RemoveRaw(Element parent, RawHtml child) : Patch
{
    public readonly Element Parent = parent;
    public readonly RawHtml Child = child;
}

/// <summary>Replace one raw HTML node with another.</summary>
public readonly struct ReplaceRaw(RawHtml oldNode, RawHtml newNode) : Patch
{
    public readonly RawHtml OldNode = oldNode;
    public readonly RawHtml NewNode = newNode;
}

/// <summary>Update the content of an existing raw HTML node.</summary>
public readonly struct UpdateRaw(RawHtml node, string html, string newId) : Patch
{
    public readonly RawHtml Node = node;
    public readonly string Html = html;
    public readonly string NewId = newId;
}
