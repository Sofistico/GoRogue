﻿using System;
using System.Collections.Generic;
using System.Linq;
using GoRogue.Components;
using GoRogue.Components.ParentAware;
using GoRogue.FOV;
using GoRogue.GridViews;
using GoRogue.Pathing;
using JetBrains.Annotations;
using SadRogue.Primitives;
using SadRogue.Primitives.GridViews;
using SadRogue.Primitives.PointHashers;
using SadRogue.Primitives.Pooling;
using SadRogue.Primitives.SpatialMaps;

namespace GoRogue.GameFramework
{
    /// <summary>
    /// Base class for a map that consists of one or more objects of base type <see cref="IGameObject" />.  It
    /// implements basic functionality to manage and access these objects, as well as commonly needed functionality like
    /// tile exploration, FOV, and pathfinding.  It also provides methods to easily access these objects as instances of
    /// some derived type.  This can be used to easily access functionality you've implemented in a subclass.  Finally,
    /// it provides the ability to attach components to the map.
    /// </summary>
    /// <remarks>
    /// A Map consists of <see cref="IGameObject" /> instances on one or more layers.  These layers are numbered, from
    /// the lowest layer of 0 upward.  Each Map contains at minimum a layer 0, which is considered the "terrain" layer.
    /// All objects added to this layer cannot move while they are added to a map; though they can move when they aren't
    /// a part of any map.
    ///
    /// A map will typically also have some other layers, for non-terrain objects like monsters, items, etc.  The number
    /// of these layers present on the map, along with which of all the layers participate in collision detection, etc.,
    /// can be specified in the constructor.
    ///
    /// If <see cref="ComponentCollection"/> (or some other custom collection implementing the proper functionality) is
    /// used, as the component collection, this object provides support for its components to (optionally) implement
    /// <see cref="IParentAwareComponent"/>, or inherit from <see cref="ParentAwareComponentBase"/>.
    /// In this case, the <see cref="IParentAwareComponent.Parent"/> will be updated automatically as components are added/
    /// removed.  Typically, you will want to inherit your components from <see cref="ParentAwareComponentBase{TParent}"/>,
    /// where TParent would be Map or some class inheriting from it.
    /// </remarks>
    [PublicAPI]
    public class Map : GridViewBase<MapObjectsAtEnumerator>, IObjectWithComponents
    {
        private readonly LayeredSpatialMap<IGameObject> _entities;
        private readonly ISettableGridView<IGameObject?> _terrain;

        private IFOV _playerFOV;
        /// <summary>
        /// FOV for the player.  By default, calculated based upon <see cref="TransparencyView"/>.
        /// <see cref="PlayerExplored"/> is updated automatically when this is calculated.
        /// </summary>
        public IFOV PlayerFOV
        {
            get => _playerFOV;

            set
            {
                if (_playerFOV == value)
                    return;

                _playerFOV.Recalculated -= On_FOVRecalculated;

                _playerFOV = value;
                _playerFOV.Recalculated += On_FOVRecalculated;
            }
        }

        /// <inheritdoc/>
        public IComponentCollection GoRogueComponents { get; }

        /// <summary>
        /// Whether or not each tile is considered explored.  Tiles start off unexplored, and become explored as soon as
        /// they are within <see cref="PlayerFOV"/>.  This ArrayView may also have values set to it, to easily allow for
        /// custom serialization or wizard-mode like functionality.
        /// </summary>
        public ArrayView<bool> PlayerExplored;

        /// <summary>
        /// Constructor.  Constructs terrain map as <see cref="SadRogue.Primitives.GridViews.ArrayView{T}" />; with the given width/height.
        /// </summary>
        /// <param name="width">Width of the map.</param>
        /// <param name="height">Height of the map.</param>
        /// <param name="numberOfEntityLayers">Number of non-terrain layers for the map.</param>
        /// <param name="distanceMeasurement">
        /// <see cref="SadRogue.Primitives.Distance" /> measurement to use for pathfinding/measuring distance on the
        /// map.
        /// </param>
        /// <param name="customListPoolCreator">
        /// A function used to determine the list pool implementation used for the spatial maps which support multiple
        /// items in a location (if any).  The function takes the layer it is creating the pool for as a parameter.
        /// If no custom creator is specified, a ListPool is used.
        /// </param>
        /// <param name="layersBlockingWalkability">
        /// Layer mask containing those layers that should be allowed to have items that block walkability.
        /// Defaults to all layers.
        /// </param>
        /// <param name="layersBlockingTransparency">
        /// Layer mask containing those layers that should be allowed to have items that block FOV.
        /// Defaults to all layers.
        /// </param>
        /// <param name="entityLayersSupportingMultipleItems">
        /// Layer mask containing those layers that should be allowed to have multiple objects at the same
        /// location on the same layer.  Defaults to all layers.
        /// </param>
        /// <param name="pointComparer">
        /// Equality comparer to use for comparison and hashing of points, as object are added to/removed from/moved
        /// around the map.  Be especially mindful of the efficiency of its GetHashCode function, as it will
        /// determine the efficiency of many AMap functions.  Defaults to a fast hashing algorithm that generates
        /// a unique integer for each point based on the width of the map.
        /// </param>
        /// <param name="customPlayerFOV">
        /// Custom FOV to use for <see cref="PlayerFOV"/>.  Defaults to a GoRogue's recursive shadow-casting
        /// implementation.  It may also be useful to specify this if you want the <see cref="PlayerFOV"/> property
        /// to not use <see cref="TransparencyView"/> for data.
        /// </param>
        /// <param name="customPather">
        /// Custom A* pathfinder for the map.  Typically, you wont' need to specify this; By default, uses
        /// <see cref="WalkabilityView" /> to determine which locations can be reached, and calculates distance based
        /// on the <see cref="SadRogue.Primitives.Distance" /> passed to the Map in the constructor.
        /// </param>
        /// <param name="customComponentCollection">
        /// A custom component container to use for <see cref="GoRogueComponents"/>.  If not specified, a
        /// <see cref="ComponentCollection"/> is used.  Typically you will not need to specify this, as a
        /// ComponentCollection is sufficient for nearly all use cases.
        /// </param>
        /// <param name="useCachedGridViews">
        /// Whether or not to use cached grid views for <see cref="TransparencyView"/> and <see cref="WalkabilityView"/>,
        /// rather than calculating values on the fly.  Calculating on the fly is notably slower, but takes up less memory.
        /// </param>
        public Map(int width, int height, int numberOfEntityLayers, Distance distanceMeasurement,
                   Func<int, IListPool<IGameObject>>? customListPoolCreator = null,
                   uint layersBlockingWalkability = uint.MaxValue,
                   uint layersBlockingTransparency = uint.MaxValue,
                   uint entityLayersSupportingMultipleItems = uint.MaxValue,
                   IEqualityComparer<Point>? pointComparer = null,
                   IFOV? customPlayerFOV = null,
                   AStar? customPather = null,
                   IComponentCollection? customComponentCollection = null,
                   bool useCachedGridViews = true)
            : this(new ArrayView<IGameObject?>(width, height), distanceMeasurement, numberOfEntityLayers,
                customListPoolCreator, layersBlockingWalkability, layersBlockingTransparency,
                entityLayersSupportingMultipleItems, pointComparer, customPlayerFOV, customPather, customComponentCollection, useCachedGridViews)
        { }

        /// <summary>
        /// Constructor.  Constructs map with the given terrain layer, determining width/height based on the
        /// width/height of that terrain layer.  Note that the terrainLayer you pass it is subject to some very specific
        /// restrictions; see the remarks for details.  Consider using <see cref="ApplyTerrainOverlay{T}"/> instead of
        /// this constructor for more use cases.
        /// </summary>
        /// <remarks>
        /// Because of the way polymorphism works for custom classes in C#, the <paramref name="terrainLayer" />
        /// parameter MUST be of type <see cref="SadRogue.Primitives.GridViews.ISettableGridView{IGameObject}" />, rather than
        /// <see cref="SadRogue.Primitives.GridViews.ISettableGridView{T}" /> where T is a type that derives from or implements
        /// <see cref="IGameObject" />.  If you need to use a map view storing some type T rather than IGameObject, use
        /// the <see cref="CreateMap{T}"/> function to create the map.
        ///
        /// Note that this constructor exists pretty much entirely for maximum compatibility; however using it is subject
        /// to a few restrictions.  Primarily, the <paramref name="terrainLayer"/> given must:
        ///
        /// 1. Always consist of persistent objects.  This is to say, the layer _cannot_ be creating objects whenever
        ///    they are requested via the indexer.  For example, a SettableLambdaGridView which creates a new GameObject
        ///    based on some other data is NOT a valid <paramref name="terrainLayer"/>.  For this use case,
        ///    <see cref="ApplyTerrainOverlay{T}"/> is recommended instead.
        ///
        /// 2. Always have its values swapped out via <see cref="SetTerrain(IGameObject)"/> function  after it is passed to this
        ///    constructor, rather than via its set indexer.  Basically, the terrain view must not be changed by anything
        ///    other than the map after this constructor takes it.
        ///
        /// Violating either of these conditions will create desync bugs that can leave the map in a completely unusable
        /// state.  Generally, this constructor should be used as a matter of last resort when there isn't a better way
        /// to accomplish your integration goals.  You should probably prefer the <see cref="ApplyTerrainOverlay{T}"/>
        /// approach until/unless you have a reason not to.
        /// </remarks>
        /// <param name="terrainLayer">
        /// The <see cref="SadRogue.Primitives.GridViews.ISettableGridView{T}" /> that represents the terrain layer for this map.  See the remarks
        /// section for some invariants that _must_ be adhered to regarding this parameter.
        /// </param>
        /// <param name="numberOfEntityLayers">Number of non-terrain layers for the map.</param>
        /// <param name="distanceMeasurement">
        /// <see cref="SadRogue.Primitives.Distance" /> measurement to use for pathfinding/measuring distance on the
        /// map.
        /// </param>
        /// <param name="customListPoolCreator">
        /// A function used to determine the list pool implementation used for the spatial maps which support multiple
        /// items in a location (if any).  The function takes the layer it is creating the pool for as a parameter.
        /// If no custom creator is specified, a ListPool is used.
        /// </param>
        /// <param name="layersBlockingWalkability">
        /// Layer mask containing those layers that should be allowed to have items that block walkability.
        /// Defaults to all layers.
        /// </param>
        /// <param name="layersBlockingTransparency">
        /// Layer mask containing those layers that should be allowed to have items that block FOV.
        /// Defaults to all layers.
        /// </param>
        /// <param name="entityLayersSupportingMultipleItems">
        /// Layer mask containing those layers that should be allowed to have multiple objects at the same
        /// location on the same layer.  Defaults to all layers.
        /// </param>
        /// <param name="pointComparer">
        /// Equality comparer to use for comparison and hashing of points, as object are added to/removed from/moved
        /// around the map.  Be especially mindful of the efficiency of its GetHashCode function, as it will
        /// determine the efficiency of many AMap functions.  Defaults to a fast hashing algorithm that generates
        /// a unique integer for each point based on the width of the map.
        /// </param>
        /// <param name="customPlayerFOV">
        /// Custom FOV to use for <see cref="PlayerFOV"/>.  Defaults to a GoRogue's recursive shadow-casting
        /// implementation.  It may also be useful to specify this if you want the <see cref="PlayerFOV"/> property
        /// to not use <see cref="TransparencyView"/> for data.
        /// </param>
        /// <param name="customPather">
        /// Custom A* pathfinder for the map.  Typically, you wont' need to specify this; By default, uses
        /// <see cref="WalkabilityView" /> to determine which locations can be reached, and calculates distance based
        /// on the <see cref="SadRogue.Primitives.Distance" /> passed to the Map in the constructor.
        /// </param>
        /// <param name="customComponentCollection">
        /// A custom component container to use for <see cref="GoRogueComponents"/>.  If not specified, a
        /// <see cref="ComponentCollection"/> is used.  Typically you will not need to specify this, as a
        /// ComponentCollection is sufficient for nearly all use cases.
        /// </param>
        /// <param name="useCachedGridViews">
        /// Whether or not to use cached grid views for <see cref="TransparencyView"/> and <see cref="WalkabilityView"/>,
        /// rather than calculating values on the fly.  Calculating on the fly is notably slower, but takes up less memory.
        /// </param>
        public Map(ISettableGridView<IGameObject?> terrainLayer, int numberOfEntityLayers, Distance distanceMeasurement,
                   Func<int, IListPool<IGameObject>>? customListPoolCreator = null,
                   uint layersBlockingWalkability = uint.MaxValue,
                   uint layersBlockingTransparency = uint.MaxValue,
                   uint entityLayersSupportingMultipleItems = uint.MaxValue,
                   IEqualityComparer<Point>? pointComparer = null,
                   IFOV? customPlayerFOV = null,
                   AStar? customPather = null,
                   IComponentCollection? customComponentCollection = null,
                   bool useCachedGridViews = true)
            : this(terrainLayer, distanceMeasurement, numberOfEntityLayers, customListPoolCreator, layersBlockingWalkability,
                layersBlockingTransparency, entityLayersSupportingMultipleItems, pointComparer, customPlayerFOV,
                customPather, customComponentCollection, useCachedGridViews)
        {
            // Ensure any initial terrain from the grid view gets initialized properly
            foreach (var pos in terrainLayer.Positions())
            {
                var terrain = _terrain[pos];
                if (terrain != null)
                    SetTerrain(terrain, false);
            }
        }

        // Note: Swaps the order of distanceMeasurement and numberOfEntityLayers to disambiguate it from the public
        // constructors.  This constructor does NOT perform any initialization on the terrain provided in the terrainLayer;
        // it effectively assumes the terrainLayer is empty.  We still want to do most of this from a constructor, since
        // many of the fields/properties are read-only outside of one, which is why we use this constructor instead of
        // a utility function.
        private Map(ISettableGridView<IGameObject?> terrainLayer, Distance distanceMeasurement, int numberOfEntityLayers,
                    Func<int, IListPool<IGameObject>>? customListPoolCreator, uint layersBlockingWalkability,
                    uint layersBlockingTransparency, uint entityLayersSupportingMultipleItems,
                   IEqualityComparer<Point>? pointComparer, IFOV? customPlayerFOV, AStar? customPather,
                   IComponentCollection? customComponentCollection, bool useCachedGridViews)
        {
            _terrain = terrainLayer;
            PlayerExplored = new ArrayView<bool>(_terrain.Width, _terrain.Height);

            pointComparer ??= new KnownSizeHasher(terrainLayer.Width);
            _entities = new LayeredSpatialMap<IGameObject>(numberOfEntityLayers, customListPoolCreator, pointComparer,
                1, entityLayersSupportingMultipleItems);

            LayersBlockingWalkability = layersBlockingWalkability;
            LayersBlockingTransparency = layersBlockingTransparency;

            _entities.ItemAdded += (s, e) => ObjectAdded?.Invoke(this, e);
            _entities.ItemRemoved += (s, e) => ObjectRemoved?.Invoke(this, e);
            _entities.ItemMoved += (s, e) => ObjectMoved?.Invoke(this, e);

            // We avoid the use of LambdaGridViews and similar here, in order to maximize performance since these
            // grid views are used very frequently.

            if (useCachedGridViews)
            {
                _cachedTransparencyView = new BitArrayView(_terrain.Width, _terrain.Height);
                TransparencyView = _cachedTransparencyView;
                _cachedWalkabilityView = new BitArrayView(_terrain.Width, _terrain.Height);
                WalkabilityView = _cachedWalkabilityView;

                _cachedTransparencyView.Fill(true);
                _cachedWalkabilityView.Fill(true);

                ObjectAdded += Map_ObjectAddedSyncViews;
                ObjectMoved += Map_ObjectMovedSyncViews;
                ObjectRemoved += Map_ObjectRemovedSyncViews;
            }
            else
            {
                TransparencyView = layersBlockingTransparency == 1
                    ? (IGridView<bool>)new TerrainOnlyMapTransparencyView(this)
                    : new FullMapTransparencyView(this);

                WalkabilityView = layersBlockingWalkability == 1
                    ? (IGridView<bool>)new TerrainOnlyMapWalkabilityView(this)
                    : new FullMapWalkabilityView(this);
            }

            _playerFOV = customPlayerFOV ?? new RecursiveShadowcastingFOV(TransparencyView);
            _playerFOV.Recalculated += On_FOVRecalculated;

            AStar = customPather ?? new AStar(WalkabilityView, distanceMeasurement);

            GoRogueComponents = customComponentCollection ?? new ComponentCollection();
            GoRogueComponents.ParentForAddedComponents = this;
        }

        #region Cached View Syncing
        private void Map_ObjectAddedSyncViews(object? sender, ItemEventArgs<IGameObject> e)
        {
            if (!e.Item.IsWalkable)
                _cachedWalkabilityView![e.Position] = false;

            if (!e.Item.IsTransparent)
                _cachedTransparencyView![e.Position] = false;

            e.Item.WalkabilityChanged += Item_WalkabilityChangedSyncView;
            e.Item.TransparencyChanged += Item_TransparencyChangedSyncView;
        }

        private void Map_ObjectMovedSyncViews(object? sender, ItemMovedEventArgs<IGameObject> e)
        {
            // Only one non-walkable item can be at any location by definition of collision detection; so setting to true instead of checking if there are any other non-walkable items at the location is ok
            if (!e.Item.IsWalkable)
            {
                _cachedWalkabilityView![e.OldPosition] = true;
                _cachedWalkabilityView![e.NewPosition] = false;
            }

            // Multiple non-transparent items can be at any location; so we need to check if there are any other non-transparent items at the old location
            if (!e.Item.IsTransparent)
            {
                _cachedTransparencyView![e.OldPosition] = LayersBlockingTransparency == 1
                    ? _terrain[e.OldPosition]?.IsTransparent ?? true
                    : FullIsTransparent(e.OldPosition);
                _cachedTransparencyView![e.NewPosition] = false;
            }
        }

        private void Map_ObjectRemovedSyncViews(object? sender, ItemEventArgs<IGameObject> e)
        {
            // Only one non-walkable item can be at any location by definition of collision detection; so setting to true instead of checking if there are any other non-walkable items at the location is ok
            if (!e.Item.IsWalkable)
                _cachedWalkabilityView![e.Position] = true;

            // Multiple non-transparent items can be at any location; so we need to check if there are any other non-transparent items at the location
            if (!e.Item.IsTransparent)
                _cachedTransparencyView![e.Position] = LayersBlockingTransparency == 1
                    ? _terrain[e.Position]?.IsTransparent ?? true
                    : FullIsTransparent(e.Position);

            e.Item.WalkabilityChanged -= Item_WalkabilityChangedSyncView;
            e.Item.TransparencyChanged -= Item_TransparencyChangedSyncView;
        }

        private void Item_WalkabilityChangedSyncView(object? sender, ValueChangedEventArgs<bool> e) => _cachedWalkabilityView![((IGameObject)sender!).Position] = e.NewValue;

        private void Item_TransparencyChangedSyncView(object? sender, ValueChangedEventArgs<bool> e)
        {
            var obj = (IGameObject)sender!;
            if (e.NewValue != e.OldValue)
                _cachedTransparencyView![obj.Position] = LayersBlockingTransparency == 1
                    ? _terrain[obj.Position]?.IsTransparent ?? true
                    : FullIsTransparent(obj.Position);
        }
        #endregion

        /// <summary>
        /// Terrain of the map.  Terrain at each location may be set via the <see cref="SetTerrain(IGameObject)" /> function.
        /// </summary>
        public IGridView<IGameObject?> Terrain => _terrain;

        /// <summary>
        /// <see cref="IReadOnlyLayeredSpatialMap{IGameObject}" /> of all entities (non-terrain objects) on the map.
        /// </summary>
        public IReadOnlyLayeredSpatialMap<IGameObject> Entities => _entities.AsReadOnly();

        /// <summary>
        /// <see cref="LayerMasker" /> that should be used to create layer masks for this Map.
        /// </summary>
        public LayerMasker LayerMasker => _entities.LayerMasker;

        /// <summary>
        /// Layer mask that contains only layers that block walkability.  A non-walkable <see cref="IGameObject" /> can
        /// only be added to this map if the layer it resides on is contained within this layer mask.
        /// </summary>
        public uint LayersBlockingWalkability { get; }

        /// <summary>
        /// Layer mask that contains only layers that block transparency.  A non-transparent <see cref="IGameObject" />
        /// can only be added to this map if the layer it is on is contained within this layer mask.
        /// </summary>
        public uint LayersBlockingTransparency { get; }

        private readonly BitArrayView? _cachedTransparencyView;
        /// <summary>
        /// <see cref="SadRogue.Primitives.GridViews.IGridView{T}" /> representing transparency values for each tile.  Each location returns true
        /// if the location is transparent (there are no non-transparent objects at that location), and false otherwise.
        /// </summary>
        public IGridView<bool> TransparencyView { get; }

        private readonly BitArrayView? _cachedWalkabilityView;
        /// <summary>
        /// <see cref="SadRogue.Primitives.GridViews.IGridView{T}" /> representing walkability values for each tile.  Each location is true if
        /// the location is walkable (there are no non-walkable objects at that location), and false otherwise.
        /// </summary>
        public IGridView<bool> WalkabilityView { get; }


        /// <summary>
        /// A* pathfinder for the map.  By default, uses <see cref="WalkabilityView" /> to determine which locations can
        /// be reached, and calculates distance based on the <see cref="SadRogue.Primitives.Distance" /> passed to the Map in the constructor.
        /// </summary>
        public AStar AStar { get; set; }

        /// <summary>
        /// <see cref="SadRogue.Primitives.Distance" /> measurement used for pathfinding and measuring distance on the map.
        /// </summary>
        public Distance DistanceMeasurement => AStar.DistanceMeasurement;

        /// <summary>
        /// Height of the map, in grid spaces.
        /// </summary>
        public override int Height => _terrain.Height;

        /// <summary>
        /// Width of the map, in grid spaces.
        /// </summary>
        public override int Width => _terrain.Width;

        /// <summary>
        /// Gets all objects at the given location, from the highest layer (layer with the highest number) down.
        /// </summary>
        /// <param name="pos">The position to retrieve objects for.</param>
        /// <returns>All objects at the given location, in order from highest layer to lowest layer.</returns>
        public override MapObjectsAtEnumerator this[Point pos] => GetObjectsAt(pos);

        /// <summary>
        /// Event that is fired whenever some object is added to the map.
        /// </summary>
        public event EventHandler<ItemEventArgs<IGameObject>>? ObjectAdded;

        /// <summary>
        /// Event that is fired whenever some object is removed from the map.
        /// </summary>
        public event EventHandler<ItemEventArgs<IGameObject>>? ObjectRemoved;

        /// <summary>
        /// Event that is fired whenever some object that is part of the map is successfully moved.
        /// </summary>
        public event EventHandler<ItemMovedEventArgs<IGameObject>>? ObjectMoved;

        /// <summary>
        /// Effectively a helper-constructor.  Constructs a map using an <see cref="SadRogue.Primitives.GridViews.ISettableGridView{T}" /> for the
        /// terrain map, where type T can be any type that implements <see cref="IGameObject" />.  Note that a Map that
        /// is constructed using this function will throw an <see cref="InvalidCastException" /> if any IGameObject is
        /// given to <see cref="SetTerrain(IGameObject)" /> that cannot be cast to type T.  Also, the terrain layer given
        /// is subject to the same restrictions as noted in the corresponding map constructor.
        /// </summary>
        /// <remarks>
        /// Suppose you have a class MyTerrain that inherits from BaseClass and implements <see cref="IGameObject" />.
        /// This construction function allows you to construct your map using an
        /// <see cref="SadRogue.Primitives.GridViews.ISettableGridView{MyTerrain}" /> instance as the terrain map, which you cannot do with the regular
        /// constructor since <see cref="SadRogue.Primitives.GridViews.ISettableGridView{MyTerrain}" /> does not satisfy the constructor's type
        /// requirement of <see cref="SadRogue.Primitives.GridViews.ISettableGridView{IGameObject}" />.
        ///
        /// Since this function under the hood creates a <see cref="SadRogue.Primitives.GridViews.SettableTranslationGridView{T1,T2}" /> that
        /// translates to/from IGameObject as needed,
        /// any change made using the map's <see cref="SetTerrain(IGameObject)" /> function will be reflected both in
        /// the map and in the original ISettableGridView.
        ///
        /// Note that the terrain view passed in is subject to the same restrictions as in the constructor which takes
        /// a custom terrain layer:
        ///
        /// 1. Always consist of persistent objects.  This is to say, the layer _cannot_ be creating objects whenever
        ///    they are requested via the indexer.  For example, a SettableLambdaGridView which creates a new GameObject
        ///    based on some other data is NOT a valid <paramref name="terrainLayer"/>.  For this use case,
        ///    <see cref="ApplyTerrainOverlay{T}"/> is recommended instead.
        ///
        /// 2. Always have its values swapped out via <see cref="SetTerrain(IGameObject)"/> function  after it is passed to this
        ///    constructor, rather than via its set indexer.  Basically, the terrain view must not be changed by anything
        ///    other than the map after this constructor takes it.
        ///
        /// Violating either of these conditions will create desync bugs that can leave the map in a completely unusable
        /// state.  Generally, this helper-constructor should be used as a matter of last resort when there isn't a better way
        /// to accomplish your integration goals.  You should probably prefer the <see cref="ApplyTerrainOverlay{T}"/>
        /// approach until/unless you have a reason not to.
        /// </remarks>
        /// <typeparam name="T">
        /// The type of terrain that will be stored in the created Map.  Can be any type that implements
        /// <see cref="IGameObject" />.
        /// </typeparam>
        /// <param name="terrainLayer">
        /// The <see cref="SadRogue.Primitives.GridViews.ISettableGridView{T}" /> that represents the terrain layer for this map.  After the
        /// map has been created, you should use the <see cref="SetTerrain(IGameObject)" /> function to modify the
        /// values in this map view, rather than setting the values via the map view itself.  If you re-assign the
        /// value at a location via the map view, the <see cref="ObjectAdded" />/<see cref="ObjectRemoved" /> events are
        /// NOT guaranteed to be called, and many invariants of map may not be properly enforced.
        /// </param>
        /// <param name="numberOfEntityLayers">Number of non-terrain layers for the map.</param>
        /// <param name="distanceMeasurement">
        /// <see cref="SadRogue.Primitives.Distance" /> measurement to use for pathfinding/measuring distance on the
        /// map.
        /// </param>
        /// <param name="customListPoolCreator">
        /// A function used to determine the list pool implementation used for the spatial maps which support multiple
        /// items in a location (if any).  The function takes the layer it is creating the pool for as a parameter.
        /// If no custom creator is specified, a ListPool is used.
        /// </param>
        /// <param name="layersBlockingWalkability">
        /// Layer mask containing those layers that should be allowed to have items that block walkability.
        /// Defaults to all layers.
        /// </param>
        /// <param name="layersBlockingTransparency">
        /// Layer mask containing those layers that should be allowed to have items that block FOV.
        /// Defaults to all layers.
        /// </param>
        /// <param name="entityLayersSupportingMultipleItems">
        /// Layer mask containing those layers that should be allowed to have multiple objects at the same
        /// location on the same layer.  Defaults to all layers.
        /// </param>
        /// /// <param name="pointComparer">
        /// Equality comparer to use for comparison and hashing of points, as object are added to/removed from/moved
        /// around the map.  Be especially mindful of the efficiency of its GetHashCode function, as it will
        /// determine the efficiency of many AMap functions.  Defaults to a fast hashing algorithm that generates
        /// a unique integer for each point based on the width of the map.
        /// </param>
        /// <param name="customPlayerFOV">
        /// Custom FOV to use for <see cref="PlayerFOV"/>.  Defaults to a GoRogue's recursive shadow-casting
        /// implementation.  It may also be useful to specify this if you want the <see cref="PlayerFOV"/> property
        /// to not use <see cref="TransparencyView"/> for data.
        /// </param>
        /// <param name="customPather">
        /// Custom A* pathfinder for the map.  Typically, you wont' need to specify this; By default, uses
        /// <see cref="WalkabilityView" /> to determine which locations can be reached, and calculates distance based
        /// on the <see cref="SadRogue.Primitives.Distance" /> passed to the Map in the constructor.
        /// </param>
        /// <param name="customComponentContainer">
        /// A custom component container to use for <see cref="GoRogueComponents"/>.  If not specified, a
        /// <see cref="ComponentCollection"/> is used.  Typically you will not need to specify this, as a
        /// ComponentCollection is sufficient for nearly all use cases.
        /// </param>
        /// <returns>A new Map whose terrain is created using the given terrainLayer, and with the given parameters.</returns>
        public static Map CreateMap<T>(ISettableGridView<T?> terrainLayer, int numberOfEntityLayers,
                                       Distance distanceMeasurement,
                                       Func<int, IListPool<IGameObject>>? customListPoolCreator = null,
                                       uint layersBlockingWalkability = uint.MaxValue,
                                       uint layersBlockingTransparency = uint.MaxValue,
                                       uint entityLayersSupportingMultipleItems = uint.MaxValue,
                                       IEqualityComparer<Point>? pointComparer = null,
                                       IFOV? customPlayerFOV = null, AStar? customPather = null,
                                       IComponentCollection? customComponentContainer = null)
            where T : class, IGameObject
        {
            // Assignment is fine here
            var terrainMap = new InheritedTypeGridView<T?,IGameObject?>(terrainLayer);
            return new Map(terrainMap, numberOfEntityLayers, distanceMeasurement, customListPoolCreator,
                layersBlockingWalkability, layersBlockingTransparency, entityLayersSupportingMultipleItems, pointComparer,
                customPlayerFOV, customPather, customComponentContainer);
        }

        private bool FullIsTransparent(Point position)
        {
            foreach (var item in GetObjectsAt(position, LayersBlockingTransparency))
                if (!item.IsTransparent)
                    return false;

            return true;
        }

        private bool FullIsWalkable(Point position)
        {
            foreach (var item in GetObjectsAt(position, LayersBlockingWalkability))
                if (!item.IsWalkable)
                    return false;

            return true;
        }

        #region Terrain

        /// <summary>
        /// Gets the terrain object at the given location, or null if no terrain is set to that location.
        /// </summary>
        /// <param name="position">The position to get the terrain for.</param>
        /// <returns>The terrain at the given position, or null if no terrain exists at that location.</returns>
        public IGameObject? GetTerrainAt(Point position) => _terrain[position];

        /// <summary>
        /// Gets the terrain object at the given location, as a value of type TerrainType.  Returns null if no terrain
        /// is set, or the terrain cannot be cast to the type specified.
        /// </summary>
        /// <typeparam name="TTerrain">Type to check for/return the terrain as.</typeparam>
        /// <param name="position">The position to get the terrain for.</param>
        /// <returns>
        /// The terrain at the given position, or null if either no terrain exists at that location or the terrain was
        /// not castable the given type.
        /// </returns>
        public TTerrain? GetTerrainAt<TTerrain>(Point position) where TTerrain : class, IGameObject
            => _terrain[position] as TTerrain;

        /// <summary>
        /// Gets the terrain object at the given location, or null if no terrain is set to that location.
        /// </summary>
        /// <param name="x">X-value of the position to get the terrain for.</param>
        /// <param name="y">Y-value of the position to get the terrain for.</param>
        /// <returns>The terrain at the given position, or null if no terrain exists at that location.</returns>
        public IGameObject? GetTerrainAt(int x, int y) => _terrain[x, y];

        /// <summary>
        /// Gets the terrain object at the given location, as a value of type TerrainType.  Returns null if no terrain is set, or
        /// the terrain cannot be cast to the type specified.
        /// </summary>
        /// <typeparam name="TTerrain">Type to return the terrain as.</typeparam>
        /// <param name="x">X-value of the position to get the terrain for.</param>
        /// <param name="y">Y-value of the position to get the terrain for.</param>
        /// <returns>
        /// The terrain at the given position, or null if either no terrain exists at that location or the terrain was
        /// not castable to <typeparamref name="TTerrain"/>.
        /// </returns>
        public TTerrain? GetTerrainAt<TTerrain>(int x, int y) where TTerrain : class, IGameObject
            => _terrain[x, y] as TTerrain;

        /// <summary>
        /// Sets the terrain at the given objects location to the given object, overwriting any terrain already present
        /// there.
        /// </summary>
        /// <remarks>
        /// A GameObject that is added as Terrain must have a Layer of 0, must not be part of a map currently, and
        /// must have a position within the bounds of the map.
        /// </remarks>
        /// <param name="terrain">
        /// Terrain to replace the current terrain with. <paramref name="terrain" /> must have its
        /// <see cref="IHasLayer.Layer" /> must be 0, or an exception will be thrown.
        /// </param>
        public void SetTerrain(IGameObject terrain) => SetTerrain(terrain, true);

        // If performStandardChecks is false, we assume we're initializing with a new terrain map view, so we'll
        // avoid firing events and performing checks that will require using the "existing" terrain layer, and also
        // assume that the terrain given is actually already in the terrain layer.
        private void SetTerrain(IGameObject terrain, bool performStandardChecks)
        {
            if (terrain.Layer != 0)
                throw new ArgumentException("Terrain for Map must reside on layer 0.", nameof(terrain));

            if (terrain.CurrentMap != null)
                throw new ArgumentException($"Cannot add terrain to more than one {nameof(Map)}.", nameof(terrain));

            if (!this.Contains(terrain.Position))
                throw new ArgumentException("Terrain added to map must be within the bounds of that map.");

            if (!terrain.IsWalkable)
                foreach (var obj in Entities.GetItemsAt(terrain.Position))
                    if (!obj.IsWalkable)
                        throw new Exception(
                            "Tried to place non-walkable terrain at a location that already has another non-walkable item.");

            if (performStandardChecks)
            {
                var oldTerrain = _terrain[terrain.Position];
                if (oldTerrain != null)
                    RemoveTerrain(oldTerrain);

                _terrain[terrain.Position] = terrain;
            }

            terrain.OnMapChanged(this);
            terrain.PositionChanging += OnGameObjectPositionChanging;
            terrain.WalkabilityChanging += OnWalkabilityChanging;
            if (performStandardChecks)
                ObjectAdded?.Invoke(this, new ItemEventArgs<IGameObject>(terrain, terrain.Position));
        }

        /// <summary>
        /// Removes the given terrain object from the map.  If the given terrain object is not a part of the map,
        /// throws <see cref="ArgumentException"/>.
        /// </summary>
        /// <param name="terrain">Terrain to remove.</param>
        public void RemoveTerrain(IGameObject terrain)
        {
            if (terrain.CurrentMap != this)
                throw new ArgumentException("A terrain object was removed from the map that had not been added.",
                    nameof(terrain));

            _terrain[terrain.Position] = null;

            terrain.PositionChanging -= OnGameObjectPositionChanging;
            terrain.WalkabilityChanging -= OnWalkabilityChanging;
            ObjectRemoved?.Invoke(this, new ItemEventArgs<IGameObject>(terrain, terrain.Position));
            terrain.OnMapChanged(null);
        }

        /// <summary>
        /// Removes the terrain at the given location and returns it.  Throws <see cref="ArgumentException"/> if no
        /// terrain object has been added at that location.
        /// </summary>
        /// <param name="position">Position to remove terrain from.</param>
        /// <returns>Terrain that was at the position given.</returns>
        public IGameObject RemoveTerrainAt(Point position)
        {
            var terrain = _terrain[position];

            if (terrain == null)
                throw new ArgumentException("Cannot remove terrain at given location because no terrain at that location has been added",
                    nameof(position));

            ObjectRemoved?.Invoke(this, new ItemEventArgs<IGameObject>(terrain, terrain.Position));
            terrain.OnMapChanged(null);

            return terrain;
        }

        /// <summary>
        /// Removes the terrain at the given location and returns it.  Throws <see cref="ArgumentException"/> if no
        /// terrain object has been added at that location.
        /// </summary>
        /// <param name="x">X-value of the position to remove terrain from.</param>
        /// <param name="y">Y-value of the position to remove terrain from.</param>
        /// <returns>Terrain that was at the position given.</returns>
        public IGameObject RemoveTerrainAt(int x, int y) => RemoveTerrainAt(new Point(x, y));

        /// <summary>
        /// Sets all terrain on the map to the result of running the given translator on the value in
        /// <paramref name="overlay" /> at the corresponding position.  Useful, for example, for applying the map view
        /// resulting from map generation to a Map as terrain.
        /// </summary>
        /// <typeparam name="T">
        /// Type of values exposed by map view to translate.  Generally inferred by the compiler.
        /// </typeparam>
        /// <param name="overlay">_grid view to translate.</param>
        /// <param name="translator">
        /// Function that translates values of the type that <paramref name="overlay" /> exposes to values
        /// of type IGameObject.
        /// </param>
        public void ApplyTerrainOverlay<T>(IGridView<T> overlay, Func<Point, T, IGameObject> translator)
        {
            var terrainOverlay = new LambdaTranslationGridView<T, IGameObject>(overlay, translator);
            ApplyTerrainOverlay(terrainOverlay);
        }

        /// <summary>
        /// Sets all terrain on the current map to be equal to the corresponding values from the map view you pass in.
        /// All terrain will in the map view will be removed from its current Map, if any, and its position edited
        /// to what it is in the <paramref name="overlay"/>, before it is added to the map.
        /// </summary>
        /// <remarks>
        /// If translation between the overlay and IGameObject is required, see the overloads of this function that
        /// take a translation function.
        /// </remarks>
        /// <param name="overlay">
        /// Grid view specifying the terrain apply to the map. Must have identical dimensions to the current map.
        /// </param>
        public void ApplyTerrainOverlay(IGridView<IGameObject?> overlay)
        {
            if (Height != overlay.Height || Width != overlay.Width)
                throw new ArgumentException("Overlay size must match current map size.");

            foreach (var pos in _terrain.Positions())
            {
                var terrain = overlay[pos];
                if (terrain == null)
                    continue;

                terrain.CurrentMap?.RemoveTerrain(terrain);
                terrain.Position = pos;

                SetTerrain(terrain);
            }
        }

        #endregion

        #region Entities

        /// <summary>
        /// Adds the given entity (non-terrain object) to its recorded location, removing it from the map it is currently a part
        /// of.  Throws ArgumentException if the entity could not be added (eg., collision detection would not allow it, etc.)
        /// </summary>
        /// <param name="entity">Entity to add.</param>
        public void AddEntity(IGameObject entity)
        {
            if (entity.CurrentMap == this)
                throw new ArgumentException(
                    $"Tried to add entity to a {GetType().Name} that was already a part of that map.",
                    nameof(entity));

            if (entity.Layer < 1)
                throw new ArgumentException(
                    $"Tried to add entity to a {GetType().Name} that had layer < 1.  Non-terrain items must have a layer >= 1.",
                    nameof(entity));

            if (!this.Contains(entity.Position))
                throw new ArgumentException(
                    $"Tried to add entity to a {GetType().Name}, but that entity's position was not within the map.",
                    nameof(entity));

            if (!entity.IsWalkable)
            {
                if (!LayerMasker.HasLayer(LayersBlockingWalkability, entity.Layer))
                    throw new ArgumentException(
                        $"Tried to add a non-walkable entity to a {GetType().Name}, but the entity's layer is not in the map's layer-mask of layers that can block walkability.",
                        nameof(entity));
                if (!WalkabilityView[entity.Position])
                    throw new ArgumentException(
                        $"Tried to add a non-walkable entity to a {GetType().Name}, but that map already has a non-walkable object at the entity's location.",
                        nameof(entity));
            }

            if (!entity.IsTransparent && !LayerMasker.HasLayer(LayersBlockingTransparency, entity.Layer))
                throw new ArgumentException(
                    $"Tried to add a non-transparent entity to a {GetType().Name}, but the entity's layer is not in the map's layer-mask of layers that can block transparency.",
                    nameof(entity));

            _entities.Add(entity, entity.Position);

            entity.CurrentMap?.RemoveEntity(entity);
            entity.OnMapChanged(this);
            entity.PositionChanging += OnGameObjectPositionChanging;
            entity.WalkabilityChanging += OnWalkabilityChanging;
        }

        /// <summary>
        /// Adds the given entity (non-terrain object) to this map, removing it from the map it is currently a part
        /// of.  Returns false if the entity could not be added (eg., collision detection would not allow it, etc.); true otherwise.
        /// </summary>
        /// <param name="entity">Entity to add.</param>
        /// <returns>True if the entity was successfully added; false otherwise.</returns>
        public bool TryAddEntity(IGameObject entity) => TryAddEntityAt(entity, entity.Position);

        /// <summary>
        /// Adds the given entity (non-terrain object) to the location specified, removing it from the map it is currently a part
        /// of.  Returns false if the entity could not be added (eg., collision detection would not allow it, etc.); true otherwise.
        /// </summary>
        /// <remarks>
        /// The entity's Position property will be set to the location specified before it is added, but only if the function returns true.  No changes
        /// will have been made to the entity if the function returns false; that is; you may assume that Moved events will not have fired,
        /// the entity will not have been removed from any map it is already a part of, etc.
        /// </remarks>
        /// <param name="entity">Entity to add.</param>
        /// <param name="position">Location to add the entity to.</param>
        /// <returns>True if the entity was successfully added to the location specified; false otherwise.</returns>
        public bool TryAddEntityAt(IGameObject entity, Point position)
        {
            if (!CanAddEntityAt(entity, position)) return false;

            // Remove the entity from its current map, if it is a part of one
            entity.CurrentMap?.RemoveEntity(entity);

            // Update the entity's position field
            entity.Position = position;

            // Add the entity to this map
            _entities.Add(entity, entity.Position);
            entity.OnMapChanged(this);
            entity.PositionChanging += OnGameObjectPositionChanging;
            entity.WalkabilityChanging += OnWalkabilityChanging;

            return true;
        }

        /// <summary>
        /// Returns true if the entity given can be added to this map at its current position; false otherwise.
        /// </summary>
        /// <param name="entity">The entity to add.</param>
        /// <returns>true if the entity given can be added to this map at its current position; false otherwise.</returns>
        public bool CanAddEntity(IGameObject entity) => CanAddEntityAt(entity, entity.Position);

        /// <summary>
        /// Returns true if the entity given can be added to this map at the position given; false otherwise.
        /// </summary>
        /// <param name="entity">The entity to add.</param>
        /// <param name="position">The position to add the entity at.</param>
        /// <returns>true if the entity given can be added to this map at the given position; false otherwise.</returns>
        public bool CanAddEntityAt(IGameObject entity, Point position)
        {
            if (entity.CurrentMap == this)
                return false;

            if (entity.Layer < 1)
                return false;

            if (!this.Contains(position))
                return false;

            if (!entity.IsWalkable)
            {
                if (!LayerMasker.HasLayer(LayersBlockingWalkability, entity.Layer))
                    return false;
                if (!WalkabilityView[position])
                    return false;
            }

            if (!entity.IsTransparent && !LayerMasker.HasLayer(LayersBlockingTransparency, entity.Layer))
                return false;

            return _entities.CanAdd(entity, position);
        }

        /// <summary>
        /// Gets the first (non-terrain) entity encountered at the given position that can be cast to the specified type, moving
        /// from the highest existing
        /// layer in the layer mask downward. Layer mask defaults to all layers. null is returned if no entities of the specified
        /// type are found, or if
        /// there are no entities at the location.
        /// </summary>
        /// <typeparam name="TEntity">Type of entities to return.</typeparam>
        /// <param name="position">Position to check get entity for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an entity.  Defaults to all layers.</param>
        /// <returns>
        /// The first entity encountered, moving from the highest existing layer in the layer mask downward, or null if there are
        /// no entities of
        /// the specified type are found.
        /// </returns>
        public TEntity? GetEntityAt<TEntity>(Point position, uint layerMask = uint.MaxValue)
            where TEntity : class, IGameObject
            => GetEntitiesAt<TEntity>(position.X, position.Y, layerMask).FirstOrDefault();

        /// <summary>
        /// Gets the first (non-terrain) entity encountered at the given position that can be cast to the specified type, moving
        /// from the highest existing
        /// layer in the layer mask downward. Layer mask defaults to all layers. null is returned if no entities of the specified
        /// type are found, or if
        /// there are no entities at the location.
        /// </summary>
        /// <typeparam name="TEntity">Type of entities to return.</typeparam>
        /// <param name="x">X-value of the position to get entity for.</param>
        /// <param name="y">Y-value of the position to get entity for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an entity.  Defaults to all layers.</param>
        /// <returns>
        /// The first entity encountered, moving from the highest existing layer in the layer mask downward, or null if there are
        /// no entities of
        /// the specified type are found.
        /// </returns>
        public TEntity? GetEntityAt<TEntity>(int x, int y, uint layerMask = uint.MaxValue)
            where TEntity : class, IGameObject
            => GetEntitiesAt<TEntity>(x, y, layerMask).FirstOrDefault();

        /// <summary>
        /// Gets all (non-terrain) entities encountered at the given position that are castable to type EntityType, in order from
        /// the highest existing layer in the layer mask downward.  Layer mask defaults to all layers.
        /// </summary>
        /// <remarks>
        /// This function returns a custom iterator which is very fast when used in a foreach loop.
        /// If you need an IEnumerable to use with LINQ or other code, the returned struct does implement that interface;
        /// however note that iterating over it this way will not perform as well as iterating directly over this object.
        /// </remarks>
        /// <typeparam name="TEntity">Type of entities to return.</typeparam>
        /// <param name="position">Position to get entities for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>
        /// All entities encountered at the given position that are castable to the given type, in order from the highest existing
        /// layer
        /// in the mask downward.
        /// </returns>
        public MapEntitiesAtCastEnumerator<TEntity> GetEntitiesAt<TEntity>(Point position, uint layerMask = uint.MaxValue)
            where TEntity : IGameObject
            => new MapEntitiesAtCastEnumerator<TEntity>(this, position, layerMask);

        /// <summary>
        /// Gets all (non-terrain) entities encountered at the given position that are castable to type EntityType, in order from
        /// the highest existing layer in the layer mask downward.  Layer mask defaults to all layers.
        /// </summary>
        /// <remarks>
        /// This function returns a custom iterator which is very fast when used in a foreach loop.
        /// If you need an IEnumerable to use with LINQ or other code, the returned struct does implement that interface;
        /// however note that iterating over it this way will not perform as well as iterating directly over this object.
        /// </remarks>
        /// <typeparam name="TEntity">Type of entities to return.</typeparam>
        /// <param name="x">X-value of the position to get entities for.</param>
        /// <param name="y">Y-value of the position to get entities for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>
        /// All entities encountered at the given position that are castable to the given type, in order from the highest existing
        /// layer
        /// in the mask downward.
        /// </returns>
        public MapEntitiesAtCastEnumerator<TEntity> GetEntitiesAt<TEntity>(int x, int y, uint layerMask = uint.MaxValue)
            where TEntity : IGameObject
            => GetEntitiesAt<TEntity>(new Point(x, y), layerMask);

        /// <summary>
        /// Removes the given entity (non-terrain object) from the map.  Throws ArgumentException if the entity was not
        /// part of this map.
        /// </summary>
        /// <param name="entity">The entity to remove from the map.</param>
        public void RemoveEntity(IGameObject entity)
        {
            _entities.Remove(entity);
            entity.OnMapChanged(null);
            entity.PositionChanging -= OnGameObjectPositionChanging;
            entity.WalkabilityChanging -= OnWalkabilityChanging;
        }

        /// <summary>
        /// Removes the given entity (non-terrain object) from the map.  Does nothing and returns false if the entity
        /// was not part of this map; returns true and removes it otherwise.
        /// </summary>
        /// <param name="entity">The entity to remove from the map.</param>
        /// <returns>True if the given entity was removed from the map; false otherwise (eg the entity given was not in the map).</returns>
        public bool TryRemoveEntity(IGameObject entity)
        {
            if (!_entities.TryRemove(entity)) return false;

            entity.OnMapChanged(null);
            entity.PositionChanging -= OnGameObjectPositionChanging;
            entity.WalkabilityChanging -= OnWalkabilityChanging;
            return true;
        }

        #endregion

        #region GetObjects

        /// <summary>
        /// Gets the first object encountered at the given position, moving from the highest existing layer in the layer mask
        /// downward.  Layer mask defaults to all layers.
        /// </summary>
        /// <param name="position">Position to get object for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>The first object encountered, moving from the highest existing layer in the layer mask downward.</returns>
        public IGameObject? GetObjectAt(Point position, uint layerMask = uint.MaxValue)
        {
            var iterator = GetObjectsAt(position, layerMask);
            return iterator.MoveNext() ? iterator.Current : null;
        }

        /// <summary>
        /// Gets the first object encountered at the given position that can be cast to the type specified, moving from the highest
        /// existing layer in the
        /// layer mask downward. Layer mask defaults to all layers.  null is returned if no objects of the specified type are
        /// found, or if there are no
        /// objects at the location.
        /// </summary>
        /// <typeparam name="TObject">Type of objects to return.</typeparam>
        /// <param name="position">Position to get object for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>
        /// The first object encountered, moving from the highest existing layer in the layer mask downward, or null if there are
        /// no objects of
        /// the specified type are found.
        /// </returns>
        public TObject? GetObjectAt<TObject>(Point position, uint layerMask = uint.MaxValue)
            where TObject : class, IGameObject
        {
            {
                var iterator = GetObjectsAt<TObject>(position, layerMask);
                return iterator.MoveNext() ? iterator.Current : null;
            }
        }

        /// <summary>
        /// Gets the first object encountered at the given position that can be cast to the specified type, moving from the highest
        /// existing layer in the
        /// layer mask downward. Layer mask defaults to all layers. null is returned if no objects of the specified type are found,
        /// or if there are no
        /// objects at the location.
        /// </summary>
        /// <param name="x">X-value of the position to get object for.</param>
        /// <param name="y">Y-value of the position to get object for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>The first object encountered, moving from the highest existing layer in the layer mask downward.</returns>
        public IGameObject? GetObjectAt(int x, int y, uint layerMask = uint.MaxValue)
            => GetObjectAt(new Point(x, y), layerMask);

        /// <summary>
        /// Gets the first object encountered at the given position that can be cast to the specified type, moving from the highest
        /// existing layer in the
        /// layer mask downward. Layer mask defaults to all layers. null is returned if no objects of the specified type are found,
        /// or if there are no
        /// objects at the location.
        /// </summary>
        /// <typeparam name="TObject">Type of objects to return.</typeparam>
        /// <param name="x">X-value of the position to get object for.</param>
        /// <param name="y">Y-value of the position to get object for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>
        /// The first object encountered, moving from the highest existing layer in the layer mask downward, or null if there are
        /// no objects of
        /// the specified type are found.
        /// </returns>
        public TObject? GetObjectAt<TObject>(int x, int y, uint layerMask = uint.MaxValue)
            where TObject : class, IGameObject
            => GetObjectAt<TObject>(new Point(x, y), layerMask);

        /// <summary>
        /// Gets all objects encountered at the given position, in order from the highest existing layer in the layer mask
        /// downward.  Layer mask defaults to all layers.
        /// </summary>
        /// <remarks>
        /// This function returns a custom iterator which is very fast when used in a foreach loop.
        /// If you need an IEnumerable to use with LINQ or other code, the returned struct does implement that interface;
        /// however note that iterating over it this way will not perform as well as iterating directly over this object.
        /// </remarks>
        /// <param name="position">Position to get objects for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>All objects encountered at the given position, in order from the highest existing layer in the mask downward.</returns>
        public MapObjectsAtEnumerator GetObjectsAt(Point position, uint layerMask = uint.MaxValue)
            => new MapObjectsAtEnumerator(this, position, layerMask);

        /// <summary>
        /// Gets all objects encountered at the given position that are castable to type ObjectType, in order from the highest
        /// existing layer in the layer mask downward. Layer mask defaults to all layers.
        /// </summary>
        /// <remarks>
        /// This function returns a custom iterator which is very fast when used in a foreach loop.
        /// If you need an IEnumerable to use with LINQ or other code, the returned struct does implement that interface;
        /// however note that iterating over it this way will not perform as well as iterating directly over this object.
        /// </remarks>
        /// <typeparam name="TObject">Type of objects to return.</typeparam>
        /// <param name="position">Position to get objects for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>
        /// All objects encountered at the given position that are castable to the given type, in order from the highest existing
        /// layer
        /// in the mask downward.
        /// </returns>
        public MapObjectsAtCastEnumerator<TObject> GetObjectsAt<TObject>(Point position, uint layerMask = uint.MaxValue)
            where TObject : class, IGameObject
            => new MapObjectsAtCastEnumerator<TObject>(this, position, layerMask);

        /// <summary>
        /// Gets all objects encountered at the given position, in order from the highest existing layer in the layer mask
        /// downward.  Layer mask defaults to all layers.
        /// </summary>
        /// <remarks>
        /// This function returns a custom iterator which is very fast when used in a foreach loop.
        /// If you need an IEnumerable to use with LINQ or other code, the returned struct does implement that interface;
        /// however note that iterating over it this way will not perform as well as iterating directly over this object.
        /// </remarks>
        /// <param name="x">X-value of the position to get objects for.</param>
        /// <param name="y">Y-value of the position to get objects for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>All objects encountered at the given position, in order from the highest existing layer in the mask downward.</returns>
        public MapObjectsAtEnumerator GetObjectsAt(int x, int y, uint layerMask = uint.MaxValue)
            => GetObjectsAt(new Point(x, y), layerMask);

        /// <summary>
        /// Gets all objects encountered at the given position that are castable to type ObjectType, in order from the highest
        /// existing layer in the layer mask downward. Layer mask defaults to all layers.
        /// </summary>
        /// <remarks>
        /// This function returns a custom iterator which is very fast when used in a foreach loop.
        /// If you need an IEnumerable to use with LINQ or other code, the returned struct does implement that interface;
        /// however note that iterating over it this way will not perform as well as iterating directly over this object.
        /// </remarks>
        /// <typeparam name="TObject">Type of objects to return.</typeparam>
        /// <param name="x">X-value of the position to get objects for.</param>
        /// <param name="y">Y-value of the position to get objects for.</param>
        /// <param name="layerMask">Layer mask for which layers can return an object.  Defaults to all layers.</param>
        /// <returns>
        /// All objects encountered at the given position that are castable to the given type, in order from the highest existing
        /// layer
        /// in the mask downward.
        /// </returns>
        public MapObjectsAtCastEnumerator<TObject> GetObjectsAt<TObject>(int x, int y, uint layerMask = uint.MaxValue)
            where TObject : class, IGameObject
            => GetObjectsAt<TObject>(new Point(x, y), layerMask);
        #endregion

        #region FOV
        private void On_FOVRecalculated(object? s, FOVRecalculatedEventArgs e)
        {
            foreach (var pos in PlayerFOV.NewlySeen)
                PlayerExplored[pos] = true;
        }
        #endregion

        #region Movement Handling

        /// <summary>
        /// Returns whether or not the given game object is allowed to move to the position specified.  The object
        /// specified must be part of the map.
        /// </summary>
        /// <param name="gameObject">Object to check.</param>
        /// <param name="newPosition">New position to check if the object can move to.</param>
        /// <returns>True if the object given can move to the given position, false otherwise.</returns>
        /// <exception cref="ArgumentException">Thrown if the given object is not part of this map.</exception>
        public bool GameObjectCanMove(IGameObject gameObject, Point newPosition)
        {
            if (gameObject.CurrentMap != this)
                throw new ArgumentException($"{nameof(GameObjectCanMove)} was called on an object that was not "
                                            + "part of the map");

            if (gameObject.Layer == 0 || !this.Contains(newPosition) ||
                !gameObject.IsWalkable && !WalkabilityView[newPosition])
                return false;

            return _entities.CanMove(gameObject, newPosition);
        }

        private void OnGameObjectPositionChanging(object? s, ValueChangedEventArgs<Point> e)
        {
            var item = (IGameObject)s!;
            // Ensure move is valid
            if (item.Layer == 0)
                throw new InvalidOperationException(
                    "Tried to move a GameObject that was added to a map as terrain.  Terrain objects cannot "
                    + " be moved while they are added to a map.");

            if (!this.Contains(e.NewValue))
                throw new InvalidOperationException($"A GameObject tried to move to {e.NewValue}, which is "
                                                    + "outside the bounds of its map.");

            if (!item.IsWalkable && !WalkabilityView[e.NewValue])
                throw new InvalidOperationException("A non-walkable GameObject tried to move to a square where "
                                                    + "it would collide with another non-walkable object.");

            // Validate move via spatial map and synchronize the object's position in the spatial map.
            // We must translate an error to InvalidOperationException so the property setters safely transform the
            // position value back to its original value if there is an error (via the SafelySetProperty function).
            try
            {
                _entities.Move(item, e.NewValue);
            }
            catch (ArgumentException exc)
            {
                throw new InvalidOperationException(exc.Message);
            }
        }
        #endregion

        #region Walkability Validation

        /// <summary>
        /// Returns whether or not the given game object is allowed to set its <see cref="IGameObject.IsWalkable"/>
        /// property to the given value. The object specified must be part of the map.
        /// </summary>
        /// <param name="gameObject">Object to check.</param>
        /// <param name="value">New value to check for walkability.</param>
        /// <returns>
        /// True if the object may set its walkability to the given value without violating collision detection; false
        /// otherwise.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if the given object is not part of this map.</exception>
        public bool GameObjectCanSetWalkability(IGameObject gameObject, bool value)
        {
            if (gameObject.CurrentMap != this)
                throw new ArgumentException($"{nameof(GameObjectCanSetWalkability)} was called on an object "
                                            + "that was not part of the map.");

            return value || WalkabilityView[gameObject.Position];
        }
        private void OnWalkabilityChanging(object? s, ValueChangedEventArgs<bool> e)
        {
            var item = (IGameObject)s!;
            if (!e.NewValue && !WalkabilityView[item.Position])
                throw new InvalidOperationException(
                    "Cannot set walkability of object to false; this would violate collision detection rules of "
                    + "the map the object resides on.");
        }
        #endregion
    }

    /// <summary>
    /// Grid view used as the <see cref="Map.TransparencyView"/> for a map when only the terrain layer can have non-transparent
    /// tiles.
    /// </summary>
    /// <remarks>
    /// This class is used instead of a <see cref="LambdaGridView{T}"/> in order to maximize performance.
    /// </remarks>
    public sealed class TerrainOnlyMapTransparencyView : GridViewBase<bool>
    {
        private readonly Map _map;

        /// <inheritdoc />
        public override int Height => _map.Height;

        /// <inheritdoc />
        public override int Width => _map.Width;

        /// <inheritdoc />
        public override bool this[Point pos] => _map.Terrain[pos]?.IsTransparent ?? true;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="map">Map which this grid view gets its data from.</param>
        public TerrainOnlyMapTransparencyView(Map map)
        {
            _map = map;
        }
    }

    /// <summary>
    /// Grid view used as the <see cref="Map.TransparencyView"/> for a map when non-terrain layers can have non-transparent objects.
    /// </summary>
    /// <remarks>
    /// This class is used instead of a <see cref="LambdaGridView{T}"/> in order to maximize performance.
    /// </remarks>
    public sealed class FullMapTransparencyView : GridViewBase<bool>
    {
        private readonly Map _map;

        /// <inheritdoc />
        public override int Height => _map.Height;

        /// <inheritdoc />
        public override int Width => _map.Width;

        /// <inheritdoc />
        public override bool this[Point pos] => FullIsTransparent(pos);

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="map">Map which this grid view gets its data from.</param>
        public FullMapTransparencyView(Map map)
        {
            _map = map;
        }

        private bool FullIsTransparent(Point position)
        {
            foreach (var item in _map.GetObjectsAt(position, _map.LayersBlockingTransparency))
                if (!item.IsTransparent)
                    return false;

            return true;
        }
    }

    /// <summary>
    /// Grid view used as the <see cref="Map.WalkabilityView"/> for a map when only the terrain layer can have non-walkable
    /// tiles.
    /// </summary>
    /// <remarks>
    /// This class is used instead of a <see cref="LambdaGridView{T}"/> in order to maximize performance.
    /// </remarks>
    public class TerrainOnlyMapWalkabilityView : GridViewBase<bool>
    {
        private readonly Map _map;

        /// <inheritdoc />
        public override int Height => _map.Height;

        /// <inheritdoc />
        public override int Width => _map.Width;

        /// <inheritdoc />
        public override bool this[Point pos] => _map.Terrain[pos]?.IsWalkable ?? true;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="map">Map which this grid view gets its data from.</param>
        public TerrainOnlyMapWalkabilityView(Map map)
        {
            _map = map;
        }
    }

    /// <summary>
    /// Grid view used as the <see cref="Map.WalkabilityView"/> for a map when non-terrain layers can have non-walkable objects.
    /// </summary>
    /// <remarks>
    /// This class is used instead of a <see cref="LambdaGridView{T}"/> in order to maximize performance.
    /// </remarks>
    public class FullMapWalkabilityView : GridViewBase<bool>
    {
        private readonly Map _map;

        /// <inheritdoc />
        public override int Height => _map.Height;

        /// <inheritdoc />
        public override int Width => _map.Width;

        /// <inheritdoc />
        public override bool this[Point pos] => FullIsWalkable(pos);

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="map">Map which this grid view gets its data from.</param>
        public FullMapWalkabilityView(Map map)
        {
            _map = map;
        }

        private bool FullIsWalkable(Point position)
        {
            foreach (var item in _map.GetObjectsAt(position, _map.LayersBlockingWalkability))
                if (!item.IsWalkable)
                    return false;

            return true;
        }
    }
}
