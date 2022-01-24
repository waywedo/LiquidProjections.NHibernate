//TODO: UPGRADE : Another FluidCaching.cs file was removed from the project root which appeared to be a link to this, causing the code to be duplicated.

using System;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
using System.Collections;

namespace FluidCaching
{
    /// <summary>container class used to hold nodes added within a descrete timeframe</summary>
    internal class AgeBag<T> where T : class
    {
        public DateTime StartTime { get; set; }

        public DateTime StopTime { get; set; }

        public Node<T> First { get; set; }

        public bool HasExpired(TimeSpan maxAge, DateTime now)
        {
            DateTime expirationPoint = now.Subtract(maxAge);

            return StartTime < expirationPoint;
        }

        public bool HasReachedMinimumAge(TimeSpan minAge, DateTime now)
        {
            return (now - StopTime) > minAge;
        }
    }
}


namespace FluidCaching
{
    /// <summary>
    /// Provides statistics about the cache.
    /// </summary>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif
    class CacheStats
    {
        private int current;
        private int totalCount;
        private long misses;
        private long hits;

        public CacheStats(int capacity, int nrBags, int bagItemLimit, TimeSpan minAge, TimeSpan maxAge, TimeSpan validatyCheckInterval)
        {
            Capacity = capacity;
            BagCount = nrBags;
            BagSize = bagItemLimit;
            MinAge = minAge;
            MaxAge = maxAge;
            CleanupInterval = validatyCheckInterval;
        }

        /// <summary>
        /// Gets the interval at which the cache will run a clean-up (if neede).
        /// </summary>
        public TimeSpan CleanupInterval { get; set; }

        /// <summary>
        /// Gets the amount of time before a cache item will be removed from the cache during a validity check.
        /// </summary>
        public TimeSpan MaxAge { get; set; }

        /// <summary>
        /// Gets the amount of time a cache item will remain in the cache (even though it might exceed the capacity).
        /// </summary>
        public TimeSpan MinAge { get; private set; }

        /// <summary>
        /// Gets the number of cached items each age bage contains.
        /// </summary>
        public int BagSize { get; private set; }

        /// <summary>
        /// Gets the number of internal age bags the cache maintains.
        /// </summary>
        public int BagCount { get; private set; }

        /// <summary>
        /// Gets the zero-based index of the oldest bag in use.
        /// </summary>
        public int OldestBagIndex { get; private set; }

        /// <summary>
        /// Gets the zero-based index of the bag that currently receives new cache items.
        /// </summary>
        public int CurrentBagIndex { get; private set; }

        /// <summary>
        /// Gets a value indicating the maximum number of items the cache should support.
        /// </summary>
        /// <remarks>
        /// The actual number of items can exceed the value of this property if certain items didn't reach the minimum
        /// retention time.
        /// </remarks>
        public int Capacity { get; set; }

        /// <summary>
        /// The current number of items in the cache.
        /// </summary>
        public int Current => current;

        /// <summary>
        /// Number of items added to the cache since it was created.
        /// </summary>
        public int SinceCreation => totalCount;

        /// <summary>
        /// Gets the number of times an item was requested from the cache which did not exist yet, since the cache
        /// was created.
        /// </summary>
        public long Misses => misses;

        /// <summary>
        /// Gets the number of times an existing item was requested from the cache since the cache
        /// was created.
        /// </summary>
        public long Hits => hits;

        /// <summary>
        /// Resets the statistics.
        /// </summary>
        public void Reset()
        {
            totalCount = 0;
            misses = 0;
            hits = 0;
            current = 0;
        }

        internal void RegisterItem()
        {
            Interlocked.Increment(ref totalCount);
            Interlocked.Increment(ref current);
        }

        internal void UnregisterItem()
        {
            Interlocked.Decrement(ref current);
        }

        internal void RegisterMiss()
        {
            Interlocked.Increment(ref misses);
        }

        internal void RegisterHit()
        {
            Interlocked.Increment(ref hits);
        }

        internal bool RequiresRebuild => (totalCount - current) > Capacity;

        internal void MarkAsRebuild(int rebuildIndexSize)
        {
            totalCount = rebuildIndexSize;
            current = rebuildIndexSize;
        }

        /// <summary>Returns a string that represents the current object.</summary>
        /// <returns>A string that represents the current object.</returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return
                $"{{" +
                $"\n\tCapacity: {Capacity} \n\tCurrent: {current} \n\tTotal: {totalCount} \n\tHits: {hits} \n\tMisses: {misses}" +
                $"\n\tOldestBagIndex: {OldestBagIndex} \n\tCurrentBagIndex: {CurrentBagIndex}" +
                $"\n}}";
        }

        internal void RegisterRawBagIndexes(int oldestBagIndex, int currentBagIndex)
        {
            CurrentBagIndex = currentBagIndex % BagCount;
            OldestBagIndex = oldestBagIndex % BagCount;
        }
    }
}


namespace FluidCaching
{
    /// <summary>
    /// FluidCache is a thread safe cache that automatically removes the items that have not been accessed for a long time.
    /// an object will never be removed if it has been accessed within the minAge timeSpan, else it will be removed if it
    /// is older than maxAge or the cache is beyond its desired size capacity.  A periodic check is made when accessing nodes that determines
    /// if the cache is out of date, and clears the cache (allowing new objects to be loaded upon next request).
    /// </summary>
    ///
    /// <remarks>
    /// Each Index provides dictionary key / value access to any object in cache, and has the ability to load any object that is
    /// not found. The Indexes use Weak References allowing objects in index to be garbage collected if no other objects are using them.
    /// The objects are not directly stored in indexes, rather, indexes hold Nodes which are linked list nodes. The LifespanMgr maintains
    /// a list of Nodes in each AgeBag which hold the objects and prevents them from being garbage collected.  Any time an object is retrieved
    /// through a Index it is marked to belong to the current AgeBag.  When the cache gets too full/old the oldest age bag is emptied moving any
    /// nodes that have been touched to the correct AgeBag and removing the rest of the nodes in the bag. Once a node is removed from the
    /// LifespanMgr it becomes elegible for garbage collection.  The Node is not removed from the Indexes immediately.  If a Index retrieves the
    /// node prior to garbage collection it is reinserted into the current AgeBag's Node list.  If it has already been garbage collected a new
    /// object gets loaded.  If the Index size exceeds twice the capacity the index is cleared and rebuilt.
    ///
    /// !!!!! THERE ARE 2 DIFFERENT LOCKS USED BY CACHE - so care is required when altering code or you may introduce deadlocks !!!!!
    ///        order of lock nesting is LifespanMgr (Monitor) / Index (ReaderWriterLock)
    /// </remarks>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif
        class FluidCache<T> where T : class
    {
        private readonly Dictionary<string, IIndexManagement<T>> indexList = new Dictionary<string, IIndexManagement<T>>();
        private readonly LifespanManager<T> lifeSpan;

        /// <summary>Constructor</summary>
        /// <param name="capacity">the normal item limit for cache (Count may exeed capacity due to minAge)</param>
        /// <param name="minAge">the minimium time after an access before an item becomes eligible for removal, during this time
        /// the item is protected and will not be removed from cache even if over capacity</param>
        /// <param name="maxAge">the max time that an object will sit in the cache without being accessed, before being removed</param>
        /// <param name="getNow">A delegate to get the current time.</param>
        /// <param name="validateCache">
        /// An optional delegate used to determine if cache is out of date. Is called before index access not more than once per 10 seconds
        /// </param>
        public FluidCache(int capacity, TimeSpan minAge, TimeSpan maxAge, GetNow getNow, IsValid validateCache = null)
        {
            lifeSpan = new LifespanManager<T>(this, capacity, minAge, maxAge, getNow)
            {
                ValidateCache = validateCache
            };
        }

        /// <summary>
        /// Gets a collection of statistics for the current cache instance.
        /// </summary>
        public CacheStats Statistics => lifeSpan.Stats;

        internal IEnumerable<IIndexManagement<T>> Indexes => indexList.Values;

        /// <summary>Retrieve a index by name</summary>
        public IIndex<TKey, T> GetIndex<TKey>(string indexName)
        {
            IIndexManagement<T> index;
            return indexList.TryGetValue(indexName, out index) ? index as IIndex<TKey, T> : null;
        }

        /// <summary>Retrieve a object by index name / key</summary>
        public Task<T> Get<TKey>(string indexName, TKey key, ItemCreator<TKey, T> item = null)
        {
            IIndex<TKey, T> index = GetIndex<TKey>(indexName);
            return index?.GetItem(key, item);
        }

        /// <summary>AddAsNode a new index to the cache</summary>
        /// <typeparam name="TKey">the type of the key value</typeparam>
        /// <param name="indexName">the name to be associated with this list</param>
        /// <param name="getKey">delegate to get key from object</param>
        /// <param name="item">delegate to load object if it is not found in index</param>
        /// <returns>the newly created index</returns>
        public IIndex<TKey, T> AddIndex<TKey>(string indexName, GetKey<T, TKey> getKey, ItemCreator<TKey, T> item = null)
        {
            var index = new Index<TKey, T>(this, lifeSpan, getKey, item);
            indexList[indexName] = index;
            return index;
        }

        /// <summary>
        /// AddAsNode an item to the cache (not needed if accessed by index)
        /// </summary>
        public void Add(T item)
        {
            AddAsNode(item);
        }

        /// <summary>
        /// AddAsNode an item to the cache
        /// </summary>
        internal INode<T> AddAsNode(T item)
        {
            if (item == null)
            {
                return null;
            }

            INode<T> node = FindExistingNode(item);

            // dupl is used to prevent total count from growing when item is already in indexes (only new Nodes)
            bool isDuplicate = (node != null) && (node.Value == item);
            if (!isDuplicate)
            {
                var newNode = new Node<T>(lifeSpan, item);

                foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in indexList)
                {
                    if (!keyValue.Value.AddItem(newNode))
                    {
                        isDuplicate = true;
                    }
                }

                lock (this)
                {
                    if (!isDuplicate)
                    {
                        node = newNode;
                        newNode.Touch();
                        lifeSpan.Stats.RegisterItem();
                    }
                    else
                    {
                        node = FindExistingNode(item);
                    }
                }
            }

            return node;
        }

        private INode<T> FindExistingNode(T item)
        {
            INode<T> node = null;
            foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in indexList)
            {
                if ((node = keyValue.Value.FindItem(item)) != null)
                {
                    break;
                }
            }

            return node;
        }

        /// <summary>Remove all items from cache</summary>
        public void Clear()
        {
            foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in indexList)
            {
                keyValue.Value.ClearIndex();
            }

            lifeSpan.Clear();
        }
    }
}

namespace FluidCaching
{
    /// <summary>
    /// Represents a delegate that the cache uses to obtain the key from a cachable item.
    /// </summary>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif

        delegate TKey GetKey<T, TKey>(T item) where T : class;
}


namespace FluidCaching
{
    /// <summary>
    /// Represents a delegate to get the current time in a unit test-friendly way.
    /// </summary>
    /// <returns></returns>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif

        delegate DateTime GetNow();
}


namespace FluidCaching
{
    /// <summary>
    /// The public wrapper for a Index
    /// </summary>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif
        interface IIndex<TKey, T> where T : class
    {
        /// <summary>
        /// Getter for index
        /// </summary>
        /// <param name="key">key to find (or load if needed)</param>
        /// <param name="createItem">
        /// An optional delegate that is used to create the actual object if it doesn't exist in the cache.
        /// </param>
        /// <returns>the object value associated with the cache</returns>
        Task<T> GetItem(TKey key, ItemCreator<TKey, T> createItem = null);

        /// <summary>Delete object that matches key from cache</summary>
        /// <param name="key">key to find</param>
        void Remove(TKey key);
    }
}

namespace FluidCaching
{
    /// <summary>
    /// Because there is no auto inheritance between generic types, this interface is used to send messages to Index objects
    /// </summary>
    internal interface IIndexManagement<T> where T : class
    {
        void ClearIndex();
        bool AddItem(INode<T> item);
        INode<T> FindItem(T item);
        int RebuildIndex();
    }
}


namespace FluidCaching
{
    /// <summary>
    /// Index provides dictionary key / value access to any object in the cache.
    /// </summary>
    internal class Index<TKey, T> : IIndex<TKey, T>, IIndexManagement<T> where T : class
    {
        private readonly FluidCache<T> owner;
        private readonly LifespanManager<T> lifespanManager;
        private Dictionary<TKey, WeakReference<INode<T>>> index;
        private readonly GetKey<T, TKey> _getKey;
        private readonly ItemCreator<TKey, T> loadItem;

        /// <summary>constructor</summary>
        /// <param name="owner">parent of index</param>
        /// <param name="lifespanManager"></param>
        /// <param name="getKey">delegate to get key from object</param>
        /// <param name="loadItem">delegate to load object if it is not found in index</param>
        public Index(FluidCache<T> owner, LifespanManager<T> lifespanManager, GetKey<T, TKey> getKey,
            ItemCreator<TKey, T> loadItem)
        {
            Debug.Assert(owner != null, "owner argument required");
            Debug.Assert(getKey != null, "GetKey delegate required");
            this.owner = owner;
            this.lifespanManager = lifespanManager;
            index = new Dictionary<TKey, WeakReference<INode<T>>>();
            _getKey = getKey;
            this.loadItem = loadItem;
            RebuildIndex();
        }

        /// <summary>Getter for index</summary>
        /// <param name="key">key to find (or load if needed)</param>
        /// <param name="createItem">
        /// An optional factory method for creating the item if it does not exist in the cache.
        /// </param>
        /// <returns>the object value associated with key, or null if not found or could not be loaded</returns>
        public async Task<T> GetItem(TKey key, ItemCreator<TKey, T> createItem = null)
        {
            INode<T> node = FindExistingNodeByKey(key);
            node?.Touch();

            ItemCreator<TKey, T> creator = createItem ?? loadItem;
            if ((node?.Value == null) && (creator != null))
            {
                Task<T> task = creator(key);
                if (task == null)
                {
                    throw new ArgumentNullException(nameof(createItem),
                        "Expected a non-null Task. Did you intend to return a null-returning Task instead?");
                }

                T value = await task;

                lock (this)
                {
                    node = FindExistingNodeByKey(key);
                    if (node?.Value == null)
                    {
                        node = owner.AddAsNode(value);
                    }

                    lifespanManager.CheckValidity();
                }
            }

            return node?.Value;
        }

        /// <summary>Delete object that matches key from cache</summary>
        /// <param name="key"></param>
        public void Remove(TKey key)
        {
            INode<T> node = FindExistingNodeByKey(key);
            if (node != null)
            {
                lock (this)
                {
                    node = FindExistingNodeByKey(key);
                    if (node != null)
                    {
                        node.RemoveFromCache();

                        lifespanManager.CheckValidity();
                    }
                }
            }
        }

        /// <summary>try to find this item in the index and return Node</summary>
        public INode<T> FindItem(T item)
        {
            return FindExistingNodeByKey(_getKey(item));
        }

        private INode<T> FindExistingNodeByKey(TKey key)
        {
            WeakReference<INode<T>> reference;
            INode<T> node;
            if (index.TryGetValue(key, out reference) && reference.TryGetTarget(out node))
            {
                lifespanManager.Stats.RegisterHit();
                return node;
            }

            return null;
        }

        /// <summary>Remove all items from index</summary>
        public void ClearIndex()
        {
            lock (this)
            {
                index.Clear();
            }
        }

        /// <summary>AddAsNode new item to index</summary>
        /// <param name="item">item to add</param>
        /// <returns>
        /// Returns <c>true</c> if the item could be added to the index, or <c>false</c> otherwise.
        /// </returns>
        public bool AddItem(INode<T> item)
        {
            lock (this)
            {
                TKey key = _getKey(item.Value);

                INode<T> node;
                if (!index.ContainsKey(key) || !index[key].TryGetTarget(out node) || node.Value == null)
                {
                    index[key] = new WeakReference<INode<T>>(item, trackResurrection: false);
                    return true;
                }

                return false;
            }
        }

        /// <summary>removes all items from index and reloads each item (this gets rid of dead nodes)</summary>
        public int RebuildIndex()
        {
            lock (this)
            {
                // Create a new ConcurrentDictionary, this way there is no need for locking the index itself
                var keyValues = lifespanManager
                    .ToDictionary(item => _getKey(item.Value), item => new WeakReference<INode<T>>(item));

                index = new Dictionary<TKey, WeakReference<INode<T>>>(keyValues);
                return index.Count;
            }
        }
    }
}

namespace FluidCaching
{
    /// <summary>
    /// This interface exposes the public part of a LifespanMgr.Node
    /// </summary>
    internal interface INode<T> where T : class
    {
        T Value { get; }
        void Touch();
        void RemoveFromCache();
    }
}

namespace FluidCaching
{
    /// <summary>
    /// Represents a method that the cache can optionally use to invalidate the entire cache based
    /// on external circumstances.
    /// </summary>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif

    delegate bool IsValid();
}


namespace FluidCaching
{
    /// <summary>
    /// Represents an async operation for creating a cachable item.
    /// </summary>
#if PUBLIC_FLUID_CACHING
    public
#else
    internal
#endif

    delegate Task<T> ItemCreator<in TKey, T>(TKey key) where T : class;
}


namespace FluidCaching
{
    internal class LifespanManager<T> : IEnumerable<INode<T>> where T : class
    {
        /// <summary>
        /// The number of bags which should be enough to store the requested capacity of items. The heuristic is that each
        /// bag should contain about 5% of the capacity.
        /// </summary>
        private const int PreferedNrOfBags = 20;

        /// <summary>
        /// Numbers of bags to keep open as a buffer when the minimum age forces more items to be there than the capacity allows.
        /// </summary>
        private const int EmptyBagsBuffer = 5;

        /// <summary>
        /// No item should be kept in the cache for more than this amount, irrespective of the consumer-provided max age.
        /// </summary>
        private readonly TimeSpan MaxMaxAge = TimeSpan.FromHours(12);

        /// <summary>
        /// Maximum interval at which we want to a validity check.
        /// </summary>
        private readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(3);

        private readonly FluidCache<T> owner;
        private readonly TimeSpan minAge;
        private readonly GetNow getNow;
        private readonly TimeSpan maxAge;
        private readonly TimeSpan validatyCheckInterval;
        private DateTime nextValidityCheck;
        private readonly int bagItemLimit;

        private readonly OrderedAgeBagCollection<T> bags;
        internal int itemsInCurrentBag;
        private int currentBagIndex;
        private int oldestBagIndex;

        public LifespanManager(FluidCache<T> owner, int capacity, TimeSpan minAge, TimeSpan maxAge, GetNow getNow)
        {
            this.owner = owner;
            this.minAge = minAge;
            this.getNow = getNow;

            this.maxAge = TimeSpan.FromMilliseconds(Math.Min(maxAge.TotalMilliseconds, MaxMaxAge.TotalMilliseconds));

            validatyCheckInterval =
                TimeSpan.FromMilliseconds(Math.Min(maxAge.TotalMilliseconds, MaxInterval.TotalMilliseconds));

            bagItemLimit = Math.Max(capacity / PreferedNrOfBags, 1);

            int nrTimeSlices = (int)(MaxMaxAge.TotalMilliseconds / MaxInterval.TotalMilliseconds);

            // NOTE: Based on 240 timeslices + 20 bags for ItemLimit + 5 bags empty buffer
            int nrBags = nrTimeSlices + PreferedNrOfBags + EmptyBagsBuffer;
            bags = new OrderedAgeBagCollection<T>(nrBags);

            Stats = new CacheStats(capacity, nrBags, bagItemLimit, minAge, this.maxAge, validatyCheckInterval);

            OpenBag(0);
        }

        public AgeBag<T> CurrentBag { get; private set; }

        public IsValid ValidateCache { get; set; }

        /// <summary>checks to see if cache is still valid and if LifespanMgr needs to do maintenance</summary>
        public void CheckValidity()
        {
            // NOTE: Monitor.Enter(this) / Monitor.Exit(this) is the same as lock(this)... We are using Monitor.TryEnter() because it
            // does not wait for a lock, if lock is currently held then skip and let next Touch perform cleanup.
            if (RequiresCleanup && Monitor.TryEnter(this))
            {
                try
                {
                    if (RequiresCleanup)
                    {
                        // if cache is no longer valid throw contents away and start over, else cleanup old items
                        if ((CurrentBagIndex > 1000000) || ((ValidateCache != null) && !ValidateCache()))
                        {
                            owner.Clear();
                        }
                        else
                        {
                            CleanUp(getNow());
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(this);
                }
            }
        }

        private bool RequiresCleanup => (itemsInCurrentBag > bagItemLimit) || (getNow() > nextValidityCheck);

        /// <summary>
        /// Remove old items or items beyond capacity from LifespanMgr allowing them to be garbage collected
        /// </summary>
        /// <remarks>
        /// Since we do not physically move items when touched we must check items in bag to determine if they should
        /// be deleted or moved. Also items that were removed by setting value to null get removed now.  Rremoving
        /// an item from LifespanMgr allows it to be garbage collected. If removed item is retrieved by index prior
        /// to GC then it will be readded to LifespanMgr.
        /// </remarks>
        private void CleanUp(DateTime now)
        {
            lock (this)
            {
                int itemsAboveCapacity = Stats.Current - Stats.Capacity;
                AgeBag<T> bag = bags[OldestBagIndex];

                while (!HasProcessedAllBags && (AlmostOutOfBags || BagNeedsCleaning(bag, itemsAboveCapacity, now)))
                {
                    itemsAboveCapacity = CleanBag(bag, itemsAboveCapacity);

                    ++OldestBagIndex;
                    bag = bags[OldestBagIndex];
                }

                ++CurrentBagIndex;
                OpenBag(CurrentBagIndex);

                EnsureIndexIsValid();
            }
        }

        private static int CleanBag(AgeBag<T> bag, int itemsAboveCapacity)
        {
            Node<T> node = bag.First;
            bag.First = null;

            while (node != null)
            {
                Node<T> nextNode = node.Next;

                node.Next = null;
                if (node.Value != null && node.Bag != null)
                {
                    if (node.Bag == bag)
                    {
                        // item has not been touched since bag was closed, so remove it from LifespanMgr
                        --itemsAboveCapacity;
                        node.RemoveFromCache();
                    }
                    else
                    {
                        // item has been touched and should be moved to correct age bag now
                        node.Next = node.Bag.First;
                        node.Bag.First = node;
                    }
                }

                node = nextNode;
            }

            return itemsAboveCapacity;
        }

        private bool BagNeedsCleaning(AgeBag<T> bag, int itemsAboveCapacity, DateTime now)
        {
            return bag.HasExpired(maxAge, now) || (itemsAboveCapacity > 0 && bag.HasReachedMinimumAge(minAge, now));
        }

        private bool HasProcessedAllBags => (OldestBagIndex == CurrentBagIndex);

        private bool AlmostOutOfBags => (CurrentBagIndex - OldestBagIndex) > (bags.Count - EmptyBagsBuffer);

        private void EnsureIndexIsValid()
        {
            // if indexes are getting too big its time to rebuild them
            if (Stats.RequiresRebuild)
            {
                foreach (IIndexManagement<T> index in owner.Indexes)
                {
                    Stats.MarkAsRebuild(index.RebuildIndex());
                }
            }
        }

        public CacheStats Stats { get; }

        private int OldestBagIndex
        {
            get { return oldestBagIndex; }
            set
            {
                oldestBagIndex = value;
                Stats.RegisterRawBagIndexes(oldestBagIndex, currentBagIndex);
            }
        }

        public int CurrentBagIndex
        {
            get { return currentBagIndex; }
            set
            {
                currentBagIndex = value;
                Stats.RegisterRawBagIndexes(oldestBagIndex, currentBagIndex);
            }
        }

        /// <summary>Remove all items from LifespanMgr and reset</summary>
        public void Clear()
        {
            lock (this)
            {
                bags.Empty();

                Stats.Reset();

                // reset age bags
                OpenBag(OldestBagIndex = 0);
            }
        }

        /// <summary>ready a new current AgeBag for use and close the previous one</summary>
        private void OpenBag(int bagNumber)
        {
            lock (this)
            {
                DateTime now = getNow();

                // close last age bag
                if (CurrentBag != null)
                {
                    CurrentBag.StopTime = now;
                }

                // open new age bag for next time slice
                CurrentBagIndex = bagNumber;

                AgeBag<T> currentBag = bags[CurrentBagIndex];
                currentBag.StartTime = now;
                currentBag.First = null;

                CurrentBag = currentBag;

                // reset counters for CheckValidity()
                nextValidityCheck = now.Add(validatyCheckInterval);
                itemsInCurrentBag = 0;
            }
        }

        /// <summary>Create item enumerator</summary>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>Create item enumerator</summary>
        public IEnumerator<INode<T>> GetEnumerator()
        {
            for (int bagNumber = CurrentBagIndex; bagNumber >= OldestBagIndex; --bagNumber)
            {
                AgeBag<T> bag = bags[bagNumber];
                // if bag.first == null then bag is empty or being cleaned up, so skip it!
                for (Node<T> node = bag.First; node != null && bag.First != null; node = node.Next)
                {
                    if (node.Value != null)
                    {
                        yield return node;
                    }
                }
            }
        }

        public Node<T> AddToHead(Node<T> node)
        {
            lock (this)
            {
                Node<T> next = CurrentBag.First;
                CurrentBag.First = node;

                Stats.RegisterMiss();

                return next;
            }
        }

        public void UnregisterFromLifespanManager()
        {
            Stats.UnregisterItem();
        }
    }
}


namespace FluidCaching
{
    /// <summary>
    /// Represents a node in a linked list of items.
    /// </summary>
    internal class Node<T> : INode<T> where T : class
    {
        private readonly LifespanManager<T> manager;

        /// <summary>constructor</summary>
        public Node(LifespanManager<T> manager, T value)
        {
            this.manager = manager;
            Value = value;
        }

        /// <summary>returns the object</summary>
        public T Value { get; private set; }

        public Node<T> Next { get; set; }

        public AgeBag<T> Bag { get; private set; }

        /// <summary>
        /// Updates the status of the node to prevent it from being dropped from cache
        /// </summary>
        public void Touch()
        {
            if ((Value != null) && (Bag != manager.CurrentBag))
            {
                RegisterWithLifespanManager();

                Bag = manager.CurrentBag;
                Interlocked.Increment(ref manager.itemsInCurrentBag);
            }
        }

        private void RegisterWithLifespanManager()
        {
            if (Bag == null)
            {
                lock (this)
                {
                    if (Bag == null)
                    {
                        // if node.AgeBag==null then the object is not currently managed by LifespanMgr so add it
                        Next = manager.AddToHead(this);
                    }
                }
            }
        }

        /// <summary>
        /// Removes the object from node, thereby removing it from all indexes and allows it to be garbage collected
        /// </summary>
        public void RemoveFromCache()
        {
            if ((Bag != null) && (Value != null))
            {
                lock (this)
                {
                    if ((Bag != null) && (Value != null))
                    {
                        manager.UnregisterFromLifespanManager();

                        Value = null;
                        Bag = null;
                        Next = null;
                    }
                }
            }
        }
    }
}


namespace FluidCaching
{
    internal class OrderedAgeBagCollection<T> where T : class
    {
        private readonly AgeBag<T>[] bags;

        public OrderedAgeBagCollection(int capacity)
        {
            bags = new AgeBag<T>[capacity];

            for (int loop = capacity - 1; loop >= 0; --loop)
            {
                bags[loop] = new AgeBag<T>();
            }
        }

        public AgeBag<T> this[int number]
        {
            get
            {
                if (number == int.MaxValue)
                {
                    throw new OverflowException("The bag number has reached its max value");
                }

                if (number < 0)
                {
                    throw new ArgumentException("The bag number must be positive");
                }

                int index = number % bags.Length;
                return bags[index];
            }
        }

        public int Count => bags.Length;

        /// <summary>
        /// Empties the bags in the current set.
        /// </summary>
        /// <remarks>
        /// Emptying here means that all nodes in all bags are disassociated from the bag.
        /// </remarks>
        public void Empty()
        {
            foreach (AgeBag<T> bag in bags)
            {
                Node<T> node = bag.First;
                bag.First = null;
                while (node != null)
                {
                    Node<T> next = node.Next;
                    node.RemoveFromCache();
                    node = next;
                }
            }
        }
    }
}

