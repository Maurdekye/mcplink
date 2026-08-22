using System.Collections;
using Elements.Core;
using FrooxEngine;

namespace McpLinkSmoke;

/// <summary>
/// Everything ISyncMember drags in that Encode.SyncMember never touches. All of it throws or
/// returns a plainly-inert value: a stub that quietly returns defaults for members the code under
/// test starts using would let a behaviour change slip past the suite unnoticed.
/// </summary>
internal abstract class FakeSyncMemberBase : ISyncMember
{
    public abstract string Name { get; }
    public bool IsDrivable => false;

    public void Initialize(World world, IWorldElement parent) => throw new NotSupportedException();
    public void Dispose() { }
    public void CopyValues(ISyncMember target) => throw new NotSupportedException();
    public void CopyValues(ISyncMember target, Action copy) => throw new NotSupportedException();
    public void CopyValues(ISyncMember target, Action<ISyncMember, ISyncMember> copy) => throw new NotSupportedException();

    public event Action<IChangeable>? Changed;

    public World World => throw new NotSupportedException();
    public IWorldElement Parent => throw new NotSupportedException();
    public RefID ReferenceID => default;
    public bool IsRemoved => false;
    public bool IsPersistent => false;
    public bool IsLocalElement => true;
    public string GetSyncMemberName(ISyncMember member) => Name;
    public void ChildChanged(IWorldElement child) { }
    public DataTreeNode Save(SaveControl control) => throw new NotSupportedException();
    public void Load(DataTreeNode node, LoadControl control) => throw new NotSupportedException();

    public void Link(ILinkRef link) => throw new NotSupportedException();
    public void InheritLink(ILinkRef link) => throw new NotSupportedException();
    public void ReleaseLink(ILinkRef link) => throw new NotSupportedException();
    public void ReleaseInheritedLink(ILinkRef link) => throw new NotSupportedException();
    public bool IsDriven => false;
    public bool IsHooked => false;
    public bool IsLinked => false;
    public ILinkRef? ActiveLink => null;
    public ILinkRef? DirectLink => null;
    public ILinkRef? InheritedLink => null;
    public IEnumerable<ILinkable> LinkableChildren => [];

    public void EndInitPhase() => throw new NotSupportedException();
    public bool IsInInitPhase => false;

    // the events exist to satisfy the interface; this keeps the compiler from warning them unused
    internal void NeverCalled() => Changed?.Invoke(this);
}

/// <summary>
/// A minimal ISyncList that exists so Encode.SyncMember's list windowing can be exercised
/// OFFLINE, with no engine. Only Count and GetElement(i) are reachable from the encoder.
/// </summary>
internal sealed class FakeSyncList(int count) : FakeSyncMemberBase, ISyncList
{
    private readonly FakeElement[] _elements =
        Enumerable.Range(0, count).Select(i => new FakeElement(i)).ToArray();

    public override string Name => "FakeList";

    public int Count => _elements.Length;
    public ISyncMember GetElement(int index) => _elements[index];

    public Type ElementType => typeof(FakeElement);
    public IEnumerable Elements => _elements;
    public int IndexOfElement(ISyncMember element) => Array.IndexOf(_elements, (FakeElement)element);
    public ISyncMember AddElement() => throw new NotSupportedException();
    public ISyncMember InsertElement(int index) => throw new NotSupportedException();
    public void RemoveElement(int index) => throw new NotSupportedException();
    public ISyncMember MoveElementToIndex(int oldIndex, int newIndex) => throw new NotSupportedException();

    public event SyncListElementsEvent? ElementsAdded;
    public event SyncListElementsEvent? ElementsRemoved;
    public event SyncListElementsEvent? ElementsRemoving;
    public event SyncListEvent? ListCleared;

    internal void NeverCalledList()
    {
        ElementsAdded?.Invoke(this, 0, 0);
        ElementsRemoved?.Invoke(this, 0, 0);
        ElementsRemoving?.Invoke(this, 0, 0);
        ListCleared?.Invoke(this);
    }
}

/// <summary>
/// A list element that is neither ISyncRef nor IField, so the encoder takes its default branch and
/// renders it via ToString() — which makes each element's identity, and therefore the exact window
/// that was returned, directly assertable.
/// </summary>
internal sealed class FakeElement(int index) : FakeSyncMemberBase
{
    public int Index { get; } = index;
    public override string Name => $"e{Index}";
    public override string ToString() => $"element#{Index}";
}
