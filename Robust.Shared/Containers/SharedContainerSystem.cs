using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Utility;

namespace Robust.Shared.Containers
{
    public abstract partial class SharedContainerSystem
    {
        [Dependency] private readonly INetManager _net = default!;
        [Dependency] private readonly SharedPhysicsSystem _physics = default!;
        [Dependency] private readonly EntityLookupSystem _lookup = default!;
        [Dependency] private readonly SharedTransformSystem _transform = default!;

        private EntityQuery<MapGridComponent> _gridQuery;
        private EntityQuery<MapComponent> _mapQuery;
        protected EntityQuery<MetaDataComponent> MetaQuery;
        protected EntityQuery<PhysicsComponent> PhysicsQuery;
        protected EntityQuery<TransformComponent> TransformQuery;

        /// <inheritdoc />
        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<EntParentChangedMessage>(OnParentChanged);
            SubscribeLocalEvent<ContainerManagerComponent, ComponentStartup>(OnStartupValidation);
            SubscribeLocalEvent<ContainerManagerComponent, ComponentGetState>(OnContainerGetState);
            SubscribeLocalEvent<ContainerManagerComponent, ComponentRemove>(OnContainerManagerRemove);

            _gridQuery = GetEntityQuery<MapGridComponent>();
            _mapQuery = GetEntityQuery<MapComponent>();
            MetaQuery = GetEntityQuery<MetaDataComponent>();
            PhysicsQuery = GetEntityQuery<PhysicsComponent>();
            TransformQuery = GetEntityQuery<TransformComponent>();
        }

        private void OnContainerGetState(EntityUid uid, ContainerManagerComponent component, ref ComponentGetState args)
        {
            Dictionary<string, ContainerManagerComponent.ContainerManagerComponentState.ContainerData> containerSet = new(component.Containers.Count);

            foreach (var container in component.Containers.Values)
            {
                var uidArr = new NetEntity[container.ContainedEntities.Count];

                for (var index = 0; index < container.ContainedEntities.Count; index++)
                {
                    uidArr[index] = GetNetEntity(container.ContainedEntities[index]);
                }

                var sContainer = new ContainerManagerComponent.ContainerManagerComponentState.ContainerData(container.GetType().Name, container.ShowContents, container.OccludesLight, uidArr);
                containerSet.Add(container.ID, sContainer);
            }

            args.State = new ContainerManagerComponent.ContainerManagerComponentState(containerSet);
        }

        private void OnContainerManagerRemove(EntityUid uid, ContainerManagerComponent component, ComponentRemove args)
        {
            foreach (var container in component.Containers.Values)
            {
                container.Shutdown(EntityManager, _net);
            }

            component.Containers.Clear();
        }

        // TODO: Make ContainerManagerComponent ECS and make these proxy methods the real deal.

        #region Proxy Methods

        public T MakeContainer<T>(EntityUid uid, string id, ContainerManagerComponent? containerManager = null)
            where T : BaseContainer
        {
            if (!Resolve(uid, ref containerManager, false))
                containerManager = AddComp<ContainerManagerComponent>(uid); // Happy Vera.

            return containerManager.MakeContainer<T>(uid, id);
        }

        public T EnsureContainer<T>(EntityUid uid, string id, out bool alreadyExisted, ContainerManagerComponent? containerManager = null)
            where T : BaseContainer
        {
            if (!Resolve(uid, ref containerManager, false))
                containerManager = AddComp<ContainerManagerComponent>(uid);

            if (TryGetContainer(uid, id, out var container, containerManager))
            {
                alreadyExisted = true;
                if (container is T cast)
                    return cast;

                throw new InvalidOperationException(
                    $"The container exists but is of a different type: {container.GetType()}");
            }

            alreadyExisted = false;
            return MakeContainer<T>(uid, id, containerManager);
        }

        public T EnsureContainer<T>(EntityUid uid, string id, ContainerManagerComponent? containerManager = null)
           where T : BaseContainer
        {
            return EnsureContainer<T>(uid, id, out _, containerManager);
        }

        public BaseContainer GetContainer(EntityUid uid, string id, ContainerManagerComponent? containerManager = null)
        {
            if (!Resolve(uid, ref containerManager))
                throw new ArgumentException("Entity does not have a ContainerManagerComponent!", nameof(uid));

            return containerManager.GetContainer(id);
        }

        public bool HasContainer(EntityUid uid, string id, ContainerManagerComponent? containerManager)
        {
            if (!Resolve(uid, ref containerManager, false))
                return false;

            return containerManager.HasContainer(id);
        }

        public bool TryGetContainer(EntityUid uid, string id, [NotNullWhen(true)] out BaseContainer? container, ContainerManagerComponent? containerManager = null)
        {
            if (Resolve(uid, ref containerManager, false))
                return containerManager.TryGetContainer(id, out container);

            container = null;
            return false;
        }

        public bool TryGetContainingContainer(EntityUid uid, EntityUid containedUid, [NotNullWhen(true)] out BaseContainer? container, ContainerManagerComponent? containerManager = null, bool skipExistCheck = false)
        {
            if (Resolve(uid, ref containerManager, false) && (skipExistCheck || Exists(containedUid)))
                return containerManager.TryGetContainer(containedUid, out container);

            container = null;
            return false;
        }

        public bool ContainsEntity(EntityUid uid, EntityUid containedUid, ContainerManagerComponent? containerManager = null)
        {
            if (!Resolve(uid, ref containerManager, false) || !Exists(containedUid))
                return false;

            return containerManager.ContainsEntity(containedUid);
        }

        public void RemoveEntity(
            EntityUid uid,
            EntityUid toremove,
            ContainerManagerComponent? containerManager = null,
            TransformComponent? containedXform = null,
            MetaDataComponent? containedMeta = null,
            bool reparent = true,
            bool force = false,
            EntityCoordinates? destination = null,
            Angle? localRotation = null)
        {
            if (!Resolve(uid, ref containerManager) || !Resolve(toremove, ref containedMeta, ref containedXform))
                return;

            containerManager.Remove(toremove, containedXform, containedMeta, reparent, force, destination, localRotation);
        }

        public ContainerManagerComponent.AllContainersEnumerable GetAllContainers(EntityUid uid, ContainerManagerComponent? containerManager = null)
        {
            if (!Resolve(uid, ref containerManager))
                return new ContainerManagerComponent.AllContainersEnumerable();

            return containerManager.GetAllContainers();
        }

        #endregion

        #region Container Helpers

        public bool TryGetContainingContainer(EntityUid uid, [NotNullWhen(true)] out BaseContainer? container, MetaDataComponent? meta = null, TransformComponent? transform = null)
        {
            container = null;

            if (!Resolve(uid, ref meta, false))
                return false;

            if ((meta.Flags & MetaDataFlags.InContainer) == MetaDataFlags.None)
                return false;

            if (!Resolve(uid, ref transform, false))
                return false;

            return TryGetContainingContainer(transform.ParentUid, uid, out container, skipExistCheck: true);
        }

        /// <summary>
        ///     Checks whether the given entity is inside of a container. This will only check if this entity's direct
        ///     parent is containing it. To recursively if the entity, or any parent, is inside a container, use <see
        ///     cref="IsEntityOrParentInContainer"/>
        /// </summary>
        /// <returns>If the entity is inside of a container.</returns>
        public bool IsEntityInContainer(EntityUid uid, MetaDataComponent? meta = null)
        {
            if (!Resolve(uid, ref meta, false))
                return false;

            return (meta.Flags & MetaDataFlags.InContainer) == MetaDataFlags.InContainer;
        }

        /// <summary>
        ///     Recursively check if the entity or any parent is inside of a container.
        /// </summary>
        /// <returns>If the entity is inside of a container.</returns>
        public bool IsEntityOrParentInContainer(
            EntityUid uid,
            MetaDataComponent? meta = null,
            TransformComponent? xform = null)
        {
            if (!MetaQuery.Resolve(uid, ref meta))
                return false;

            if ((meta.Flags & MetaDataFlags.InContainer) == MetaDataFlags.InContainer)
                return true;

            if (!TransformQuery.Resolve(uid, ref xform))
                return false;

            if (!xform.ParentUid.Valid)
                return false;

            return IsEntityOrParentInContainer(xform.ParentUid);
        }

        /// <summary>
        ///     Finds the first instance of a component on the recursive parented containers that hold an entity
        /// </summary>
        public bool TryFindComponentOnEntityContainerOrParent<T>(
            EntityUid uid,
            EntityQuery<T> entityQuery,
            [NotNullWhen(true)] ref T? foundComponent,
            MetaDataComponent? meta = null,
            TransformComponent? xform = null) where T : IComponent
        {
            if (!MetaQuery.Resolve(uid, ref meta))
                return false;

            if ((meta.Flags & MetaDataFlags.InContainer) != MetaDataFlags.InContainer)
                return false;

            if (!TransformQuery.Resolve(uid, ref xform))
                return false;

            if (!xform.ParentUid.Valid)
                return false;

            if (entityQuery.Resolve(xform.ParentUid, ref foundComponent, false))
                return true;

            return TryFindComponentOnEntityContainerOrParent(xform.ParentUid, entityQuery, ref foundComponent);
        }

        /// <summary>
        ///     Finds all instances of a component on the recursive parented containers that hold an entity
        /// </summary>
        public bool TryFindComponentsOnEntityContainerOrParent<T>(
            EntityUid uid,
            EntityQuery<T> entityQuery,
            List<T> foundComponents,
            MetaDataComponent? meta = null,
            TransformComponent? xform = null) where T : IComponent
        {
            if (!MetaQuery.Resolve(uid, ref meta))
                return foundComponents.Any();

            if ((meta.Flags & MetaDataFlags.InContainer) != MetaDataFlags.InContainer)
                return foundComponents.Any();

            if (!TransformQuery.Resolve(uid, ref xform))
                return foundComponents.Any();

            if (!xform.ParentUid.Valid)
                return foundComponents.Any();

            if (TryComp(xform.ParentUid, out T? foundComponent))
                foundComponents.Add(foundComponent);

            return TryFindComponentsOnEntityContainerOrParent(xform.ParentUid, entityQuery, foundComponents);
        }

        /// <summary>
        ///     Returns true if the two entities are not contained, or are contained in the same container.
        /// </summary>
        public bool IsInSameOrNoContainer(EntityUid user, EntityUid other)
        {
            var isUserContained = TryGetContainingContainer(user, out var userContainer);
            var isOtherContained = TryGetContainingContainer(other, out var otherContainer);

            // Both entities are not in a container
            if (!isUserContained && !isOtherContained) return true;

            // Both entities are in different contained states
            if (isUserContained != isOtherContained) return false;

            // Both entities are in the same container
            return userContainer == otherContainer;
        }

        /// <summary>
        ///     Returns true if the two entities are not contained, or are contained in the same container, or if one
        ///     entity contains the other (i.e., is the parent).
        /// </summary>
        public bool IsInSameOrParentContainer(EntityUid user, EntityUid other)
        {
            var isUserContained = TryGetContainingContainer(user, out var userContainer);
            var isOtherContained = TryGetContainingContainer(other, out var otherContainer);

            // Both entities are not in a container
            if (!isUserContained && !isOtherContained) return true;

            // One contains the other
            if (userContainer?.Owner == other || otherContainer?.Owner == user) return true;

            // Both entities are in different contained states
            if (isUserContained != isOtherContained) return false;

            // Both entities are in the same container
            return userContainer == otherContainer;
        }

        /// <summary>
        ///     Check whether a given entity can see another entity despite whatever containers they may be in.
        /// </summary>
        /// <remarks>
        ///     This is effectively a variant of <see cref="IsInSameOrParentContainer"/> that also checks whether the
        ///     containers are transparent. Additionally, an entity can "see" the entity that contains it, but unless
        ///     otherwise specified the containing entity cannot see into itself. For example, a human in a locker can
        ///     see the locker and other items in that locker, but the human cannot see their own organs.  Note that
        ///     this means that the two entity arguments are NOT interchangeable.
        /// </remarks>
        public bool IsInSameOrTransparentContainer(
            EntityUid user,
            EntityUid other,
            BaseContainer? userContainer = null,
            BaseContainer? otherContainer = null,
            bool userSeeInsideSelf = false)
        {
            if (userContainer == null)
                TryGetContainingContainer(user, out userContainer);

            if (otherContainer == null)
                TryGetContainingContainer(other, out otherContainer);

            // Are both entities in the same container (or none)?
            if (userContainer == otherContainer) return true;

            // Is the user contained in the other entity?
            if (userContainer?.Owner == other) return true;

            // Does the user contain the other and can they see through themselves?
            if (userSeeInsideSelf && otherContainer?.Owner == user) return true;

            // Next we check for see-through containers. This uses some recursion, but it should be fine unless people
            // start spawning in glass matryoshka dolls.

            // Is the user in a see-through container?
            if (userContainer?.ShowContents ?? false)
                return IsInSameOrTransparentContainer(userContainer.Owner, other, otherContainer: otherContainer);

            // Is the other entity in a see-through container?
            if (otherContainer?.ShowContents ?? false)
                return IsInSameOrTransparentContainer(user, otherContainer.Owner, userContainer: userContainer, userSeeInsideSelf: userSeeInsideSelf);

            return false;
        }

        /// <summary>
        /// Gets the top-most container in the hierarchy for this entity, if it exists.
        /// </summary>
        public bool TryGetOuterContainer(EntityUid uid, TransformComponent xform, [NotNullWhen(true)] out BaseContainer? container)
        {
            var xformQuery = GetEntityQuery<TransformComponent>();
            return TryGetOuterContainer(uid, xform, out container, xformQuery);
        }

        public bool TryGetOuterContainer(EntityUid uid, TransformComponent xform,
            [NotNullWhen(true)] out BaseContainer? container, EntityQuery<TransformComponent> xformQuery)
        {
            container = null;

            if (!uid.IsValid())
                return false;

            var conQuery = GetEntityQuery<ContainerManagerComponent>();
            var metaQuery = GetEntityQuery<MetaDataComponent>();
            var child = uid;
            var parent = xform.ParentUid;

            while (parent.IsValid())
            {
                if (((metaQuery.GetComponent(child).Flags & MetaDataFlags.InContainer) == MetaDataFlags.InContainer) &&
                    conQuery.TryGetComponent(parent, out var conManager) &&
                    conManager.TryGetContainer(child, out var parentContainer))
                {
                    container = parentContainer;
                }

                var parentXform = xformQuery.GetComponent(parent);
                child = parent;
                parent = parentXform.ParentUid;
            }

            return container != null;
        }

        /// <summary>
        /// Attempts to remove an entity from its container, if any.
        /// </summary>
        /// <param name="entity">Entity that might be inside a container.</param>
        /// <param name="force">Whether to forcibly remove the entity from the container.</param>
        /// <param name="wasInContainer">Whether the entity was actually inside a container or not.</param>
        /// <returns>If the entity could be removed. Also returns false if it wasn't inside a container.</returns>
        public bool TryRemoveFromContainer(EntityUid entity, bool force, out bool wasInContainer)
        {
            DebugTools.Assert(Exists(entity));

            if (TryGetContainingContainer(entity, out var container))
            {
                wasInContainer = true;

                if (!force)
                    return container.Remove(entity);

                container.Remove(entity, EntityManager, force: true);
                return true;
            }

            wasInContainer = false;
            return false;
        }

        /// <summary>
        /// Attempts to remove an entity from its container, if any.
        /// </summary>
        /// <param name="entity">Entity that might be inside a container.</param>
        /// <param name="force">Whether to forcibly remove the entity from the container.</param>
        /// <returns>If the entity could be removed. Also returns false if it wasn't inside a container.</returns>
        public bool TryRemoveFromContainer(EntityUid entity, bool force = false)
        {
            return TryRemoveFromContainer(entity, force, out _);
        }

        /// <summary>
        /// Attempts to remove all entities in a container. Returns removed entities.
        /// </summary>
        public List<EntityUid> EmptyContainer(
            BaseContainer container,
            bool force = false,
            EntityCoordinates? destination = null,
            bool reparent = true)
        {
            var removed = new List<EntityUid>(container.ContainedEntities);
            for (var i = removed.Count - 1; i >= 0; i--)
            {
                if (container.Remove(removed[i], EntityManager, reparent: reparent, force: force, destination: destination))
                    continue;

                // failed to remove entity.
                DebugTools.Assert(container.Contains(removed[i]));
                removed.RemoveSwap(i);
            }

            return removed;
        }

        /// <summary>
        /// Attempts to remove and delete all entities in a container.
        /// </summary>
        public void CleanContainer(BaseContainer container)
        {
            foreach (var ent in container.ContainedEntities.ToArray())
            {
                if (Deleted(ent)) continue;
                container.Remove(ent, EntityManager, force: true);
                Del(ent);
            }
        }

        public void AttachParentToContainerOrGrid(Entity<TransformComponent> transform)
        {
            // TODO make this check upwards for any container, and parent to that.
            // Currently this just checks the direct parent, so entities will still teleport through containers.

            if (!transform.Comp.ParentUid.IsValid()
                || !TryGetContainingContainer(transform.Comp.ParentUid, out var container)
                || !TryInsertIntoContainer(transform, container))
                transform.Comp.AttachToGridOrMap();
        }

        private bool TryInsertIntoContainer(Entity<TransformComponent> transform, BaseContainer container)
        {
            if (container.Insert(transform)) return true;

            if (Transform(container.Owner).ParentUid.IsValid()
                && TryGetContainingContainer(container.Owner, out var newContainer))
                return TryInsertIntoContainer(transform, newContainer);

            return false;
        }

        internal bool TryGetManagerComp(EntityUid entity, [NotNullWhen(true)] out ContainerManagerComponent? manager)
        {
            DebugTools.Assert(Exists(entity));

            if (TryComp(entity, out manager))
                return true;

            // RECURSION ALERT
            var transform = Transform(entity);
            if (transform.ParentUid.IsValid())
                return TryGetManagerComp(transform.ParentUid, out manager);

            return false;
        }

        #endregion

        protected virtual void OnParentChanged(ref EntParentChangedMessage message)
        {
            var meta = MetaData(message.Entity);
            if ((meta.Flags & MetaDataFlags.InContainer) == 0)
                return;

            // Eject entities from their parent container if the parent change is done via setting the transform.
            if (TryComp(message.OldParent, out ContainerManagerComponent? containerManager))
                containerManager.Remove(message.Entity, message.Transform, meta,  reparent: false, force: true);
        }
    }
}
