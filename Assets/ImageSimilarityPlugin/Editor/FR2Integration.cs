using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// FR2 (Find Reference 2) 集成层。
    /// 通过反射调用 FR2 API，提供 FR2 检测、引用计数、角标绘制、
    /// 以及引用查找（优先 FR2，回退原生 Unity API）等功能。
    /// 所有方法均为静态，引用数缓存为会话级。
    /// </summary>
    public static class FR2Integration
    {
        private static bool? _fr2Available;
        private static readonly Dictionary<string, int> _refCountCache = new Dictionary<string, int>();
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        private static readonly HashSet<string> _missingTypes = new HashSet<string>();
        private static FR2StatusSnapshot _statusSnapshot;
        private static double _nextStatusRefreshTime;
        private static object _fr2CacheAsset;
        private static bool _fr2CacheAssetExists;
        private static bool _fr2CacheAssetLookupCompleted;
        private static PropertyInfo _fr2IsReadyProperty;
        private static MethodInfo _fr2GetUsedByCountMethod;
        private static MethodInfo _fr2RefreshMethod;
        private static GUIStyle _badgeLabelStyle;
        private static int _lastObservedCacheTimestamp = int.MinValue;
        private static readonly HashSet<string> _pendingRefCountPaths = new HashSet<string>();
        private static Action<bool> _pendingRefreshCallbacks;
        private static double _refCountRefreshDeadline;
        private static bool _refreshObservedPending;
        private static int _refreshPollCount;

        private const double READY_STATUS_REFRESH_INTERVAL = 2d;
        private const double PENDING_STATUS_REFRESH_INTERVAL = 0.5d;
        private const double REFERENCE_REFRESH_TIMEOUT = 60d;

        public sealed class FR2StatusSnapshot
        {
            public bool Installed;
            public bool Ready;
            public bool HasCacheAsset;
            public bool HasCacheData;
            public bool HasPendingChanges;
            public int AssetMapCount;
            public int AssetListCount;
            public int CacheTimestamp;
            public string Status;
            public string CacheStatus;
            public string Label;
            public string Tooltip;
        }

        // ==================================================================
        //  FR2 检测
        // ==================================================================

        /// <summary>
        /// FR2 是否已安装并且 API 可用。
        /// 当前项目的 FR2 通过 vietlabs.fr2.FR2 暴露公开门面，旧版本才使用 FR2_Cache 入口。
        /// </summary>
        public static bool HasFR2()
        {
            if (_fr2Available.HasValue) return _fr2Available.Value;

            try
            {
                _fr2Available = GetFR2FacadeType() != null || FindTypeInAllAssemblies("FR2_Cache") != null;
                Debug.Log($"[ImageSimilarityPlugin] FR2 {(_fr2Available.Value ? "已检测到" : "未检测到")}。");
            }
            catch
            {
                _fr2Available = false;
            }
            return _fr2Available.Value;
        }

        /// <summary>
        /// FR2 是否已就绪。优先信任 FR2 公开 API，旧版才退回到 AssetMap 数量判断。
        /// </summary>
        public static bool IsReady
        {
            get => GetStatus().Ready;
        }

        /// <summary>
        /// FR2 是否已有缓存数据。用于区分"确实无缓存"和"缓存存在但 FR2 仍在异步初始化"。
        /// </summary>
        public static bool HasCacheData
        {
            get => GetStatus().HasCacheData;
        }

        /// <summary>
        /// 获取 FR2 当前状态快照，用于 UI 区分"无缓存文件"、"缓存已存在但仍在初始化"和"已就绪"。
        /// </summary>
        public static FR2StatusSnapshot GetStatus()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_statusSnapshot != null && now < _nextStatusRefreshTime)
                return _statusSnapshot;

            bool wasReady = _statusSnapshot != null && _statusSnapshot.Ready;
            FR2StatusSnapshot snapshot = CaptureStatus();
            bool cacheTimestampChanged = _lastObservedCacheTimestamp != int.MinValue
                && snapshot.CacheTimestamp != _lastObservedCacheTimestamp;
            _lastObservedCacheTimestamp = snapshot.CacheTimestamp;
            _statusSnapshot = snapshot;
            _nextStatusRefreshTime = now + (snapshot.Ready
                ? READY_STATUS_REFRESH_INTERVAL
                : PENDING_STATUS_REFRESH_INTERVAL);

            // 时间戳覆盖“刷新在两次状态采样之间完成”的情况，避免漏掉 Ready 状态转换。
            if (cacheTimestampChanged || (snapshot.Ready && !wasReady))
                _refCountCache.Clear();

            return snapshot;
        }

        /// <summary>
        /// 采集一次 FR2 状态。调用方通过 GetStatus 复用快照，避免 OnGUI 反复执行反射和 AssetDatabase 查询。
        /// </summary>
        private static FR2StatusSnapshot CaptureStatus()
        {
            var snapshot = new FR2StatusSnapshot();
            Type cacheType = FindTypeInAllAssemblies("FR2_Cache");
            snapshot.Installed = GetFR2FacadeType() != null || cacheType != null;
            _fr2Available = snapshot.Installed;

            if (!snapshot.Installed)
            {
                snapshot.Label = " FR2 未安装";
                snapshot.Tooltip = "未检测到 FR2 程序集。";
                return snapshot;
            }

            if (TryGetFR2FacadeReady(out bool ready))
            {
                snapshot.Ready = ready;
            }
            else
            {
                try
                {
                    object assetMap = GetFR2AssetMap(GetFR2Cache());
                    snapshot.Ready = GetCollectionCount(assetMap) > 0;
                }
                catch { }
            }

            if (cacheType != null)
            {
                snapshot.Status = GetStaticMemberString(cacheType, "status");
                snapshot.CacheStatus = GetStaticMemberString(cacheType, "cacheStatus");
                snapshot.HasPendingChanges = GetStaticMemberBool(cacheType, "hasDirtyAsset")
                    || string.Equals(snapshot.CacheStatus, "PendingChanges", StringComparison.Ordinal);
            }

            try
            {
                object cache = GetFR2Cache(out bool hasCacheAsset);
                snapshot.HasCacheAsset = hasCacheAsset || CacheStatusIndicatesExistingCache(snapshot.CacheStatus);
                snapshot.AssetMapCount = GetCollectionCount(GetFR2AssetMap(cache));
                snapshot.AssetListCount = GetCollectionCount(GetFR2AssetList(cache));
                snapshot.CacheTimestamp = GetIntMember(cache, "_timeStamp");
                snapshot.HasCacheData = snapshot.AssetMapCount > 0 || snapshot.AssetListCount > 0;
            }
            catch { }

            FillStatusText(snapshot);
            return snapshot;
        }

        // ==================================================================
        //  引用计数与角标
        // ==================================================================

        /// <summary>
        /// 获取指定图片文件被多少资产引用（FR2 的 UsedBy 计数）。
        /// 结果缓存在会话中以便缩略图重复绘制时复用。
        /// </summary>
        public static int GetReferenceCount(string absolutePath)
        {
            // 未就绪时 FR2 API 会记录警告；此时不缓存 0，待初始化完成后再查询。
            if (!IsReady) return 0;

            string assetPath = PluginUtils.AbsoluteToAssetPath(absolutePath);
            if (string.IsNullOrEmpty(assetPath)) return 0;
            if (_refCountCache.TryGetValue(assetPath, out int cached)) return cached;

            int count = 0;
            try
            {
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) { _refCountCache[assetPath] = 0; return 0; }

                if (TryGetReferenceCountWithFR2Facade(guid, out count))
                {
                    _refCountCache[assetPath] = count;
                    return count;
                }

                object assetMap = GetFR2AssetMap(GetFR2Cache());
                if (assetMap == null) { _refCountCache[assetPath] = 0; return 0; }

                var indexer = assetMap.GetType().GetProperty("Item");
                object fr2Asset = indexer?.GetValue(assetMap, new object[] { guid });
                if (fr2Asset == null) { _refCountCache[assetPath] = 0; return 0; }

                var findUsedBy = fr2Asset.GetType().GetMethod("FindUsedBy",
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                if (findUsedBy != null)
                {
                    var list = findUsedBy.Invoke(null, new[] { fr2Asset }) as System.Collections.IList;
                    count = list?.Count ?? 0;
                }
                else
                {
                    var usedByMap = GetMemberValue(fr2Asset, "UsedByMap",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                    var countProp = usedByMap?.GetType().GetProperty("Count");
                    count = (int)(countProp?.GetValue(usedByMap) ?? 0);
                }
            }
            catch { }

            _refCountCache[assetPath] = count;
            return count;
        }

        /// <summary>清除引用数缓存（在 FR2 缓存重建后调用）</summary>
        public static void ClearRefCountCache() => _refCountCache.Clear();

        /// <summary>
        /// 新结果展示前清除插件计数；FR2 有待处理资产时自动刷新其 UsedBy 索引。
        /// </summary>
        public static bool RefreshReferenceCountsIfPending(IEnumerable<string> imagePaths,
            Action<bool> onCompleted = null)
        {
            ClearRefCountCache();
            // 扫描完成是明确的同步边界，必须绕过低频状态快照读取最新 dirty assets。
            _statusSnapshot = null;
            _nextStatusRefreshTime = 0d;
            if (!GetStatus().HasPendingChanges) return false;
            return RefreshReferenceCounts(imagePaths, onCompleted);
        }

        /// <summary>
        /// 刷新 FR2 索引，并在 UsedBy 映射重建完成后重新查询受影响图片的引用数。
        /// 返回 false 表示 FR2 不可用或无法启动刷新；Prefab 修改结果不受影响。
        /// </summary>
        public static bool RefreshReferenceCounts(IEnumerable<string> imagePaths,
            Action<bool> onCompleted = null)
        {
            InvalidateReferenceCounts(imagePaths);
            FR2StatusSnapshot currentStatus = GetStatus();
            bool hasUsableCache = currentStatus.Ready
                || currentStatus.HasCacheAsset
                || currentStatus.HasCacheData;
            if (!HasFR2() || !hasUsableCache || !TryRequestFR2Refresh())
            {
                _pendingRefCountPaths.Clear();
                return false;
            }

            if (onCompleted != null)
                _pendingRefreshCallbacks += onCompleted;

            _refreshObservedPending = false;
            _refreshPollCount = 0;
            _refCountRefreshDeadline = EditorApplication.timeSinceStartup + REFERENCE_REFRESH_TIMEOUT;
            _statusSnapshot = null;
            _nextStatusRefreshTime = 0d;

            EditorApplication.update -= PollReferenceCountRefresh;
            EditorApplication.update += PollReferenceCountRefresh;
            return true;
        }

        private static void InvalidateReferenceCounts(IEnumerable<string> imagePaths)
        {
            if (imagePaths == null) return;
            foreach (string imagePath in imagePaths)
            {
                string assetPath = PluginUtils.AbsoluteToAssetPath(imagePath);
                if (string.IsNullOrEmpty(assetPath)) continue;
                _refCountCache.Remove(assetPath);
                _pendingRefCountPaths.Add(assetPath);
            }
        }

        private static bool TryRequestFR2Refresh()
        {
            try
            {
                Type facadeType = GetFR2FacadeType();
                if (facadeType != null)
                {
                    if (_fr2RefreshMethod == null)
                        _fr2RefreshMethod = facadeType.GetMethod(
                            "Refresh",
                            BindingFlags.Public | BindingFlags.Static,
                            null,
                            Type.EmptyTypes,
                            null);
                    if (_fr2RefreshMethod != null)
                    {
                        _fr2RefreshMethod.Invoke(null, null);
                        return true;
                    }
                }

                // 旧版没有公开门面时，回退到 FR2_Cache.Check4Changes(bool)。
                Type cacheType = FindTypeInAllAssemblies("FR2_Cache");
                MethodInfo checkChanges = cacheType?.GetMethod(
                    "Check4Changes",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(bool) },
                    null);
                if (checkChanges == null) return false;
                checkChanges.Invoke(null, new object[] { true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ImageSimilarityPlugin] 无法刷新 FR2 引用缓存: {ex.Message}");
                return false;
            }
        }

        private static void PollReferenceCountRefresh()
        {
            _refreshPollCount++;
            FR2StatusSnapshot status = GetStatus();
            if (!status.Ready)
                _refreshObservedPending = true;

            // 至少等待两个 Editor update，避免在 FR2 尚未切换状态时误判为已完成。
            if (status.Ready && (_refreshObservedPending || _refreshPollCount >= 2))
            {
                CompleteReferenceCountRefresh(true);
                return;
            }

            if (EditorApplication.timeSinceStartup >= _refCountRefreshDeadline)
                CompleteReferenceCountRefresh(false);
        }

        private static void CompleteReferenceCountRefresh(bool success)
        {
            EditorApplication.update -= PollReferenceCountRefresh;

            // FR2 重建期间可能有界面尝试读取角标，完成后再次精准失效以保证重新查询。
            foreach (string assetPath in _pendingRefCountPaths)
                _refCountCache.Remove(assetPath);
            _pendingRefCountPaths.Clear();

            Action<bool> callbacks = _pendingRefreshCallbacks;
            _pendingRefreshCallbacks = null;
            if (callbacks == null) return;

            foreach (Action<bool> callback in callbacks.GetInvocationList())
            {
                try { callback(success); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ImageSimilarityPlugin] FR2 刷新完成回调失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 在缩略图矩形区域右上角绘制 FR2 引用数角标。
        /// 蓝色背景方块 + 白色数字，超过 99 显示 "99+"。
        /// </summary>
        public static void DrawRefCountBadge(Rect thumbRect, string imagePath)
        {
            if (!HasFR2()) return;
            int count = GetReferenceCount(imagePath);
            if (count <= 0) return;

            float badgeSize = 20f;
            Rect badgeRect = new Rect(thumbRect.xMax - badgeSize - 2, thumbRect.y + 2, badgeSize, badgeSize);

            Color old = GUI.color;
            GUI.color = new Color(0.2f, 0.5f, 0.9f, 0.85f);
            GUI.Box(badgeRect, "", EditorStyles.helpBox);

            if (_badgeLabelStyle == null)
            {
                _badgeLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 10,
                    normal = { textColor = Color.white }
                };
            }
            GUI.color = Color.white;
            GUI.Label(badgeRect, count > 99 ? "99+" : count.ToString(), _badgeLabelStyle);
            GUI.color = old;
        }

        // ==================================================================
        //  引用查找
        // ==================================================================

        /// <summary>
        /// 查找所有引用了指定图片列表的 Prefab 资产路径。
        /// 优先使用 FR2（秒级），不可用时回退原生 AssetDatabase 全量扫描。
        /// </summary>
        /// <param name="oldImagePaths">要查找引用的旧图片绝对路径列表</param>
        /// <param name="progressCallback">进度回调（prefabIndex, totalCount），用于更新 UI</param>
        /// <returns>引用了这些图片的 Prefab 路径列表</returns>
        public static List<string> FindPrefabsReferencing(List<string> oldImagePaths,
            Action<int, int> progressCallback = null)
        {
            var oldAssetPaths = new HashSet<string>();
            foreach (var p in oldImagePaths)
            {
                string ap = PluginUtils.AbsoluteToAssetPath(p);
                if (!string.IsNullOrEmpty(ap)) oldAssetPaths.Add(ap);
            }
            if (oldAssetPaths.Count == 0) return new List<string>();

            if (HasFR2())
            {
                var result = FindWithFR2(oldAssetPaths);
                if (result.Count > 0) return result;
            }
            return FindWithNativeAPI(oldAssetPaths, progressCallback);
        }

        /// <summary>
        /// 使用 FR2 的 FindUsedBy API 查找引用。
        /// 如果 FR2 缓存为空或查找失败，返回空列表（外部回退到原生扫描）。
        /// </summary>
        private static List<string> FindWithFR2(HashSet<string> oldAssetPaths)
        {
            var facadeResult = FindWithFR2Facade(oldAssetPaths);
            if (facadeResult.Count > 0) return facadeResult;

            var result = new HashSet<string>();
            try
            {
                object cache = GetFR2Cache();
                object assetMap = GetFR2AssetMap(cache);
                if (assetMap == null) return new List<string>();

                var indexer = assetMap.GetType().GetProperty("Item");
                Type fr2AssetType = null;
                MethodInfo findUsedByMethod = null;

                foreach (var assetPath in oldAssetPaths)
                {
                    try
                    {
                        string guid = AssetDatabase.AssetPathToGUID(assetPath);
                        if (string.IsNullOrEmpty(guid)) continue;

                        object fr2Asset = indexer?.GetValue(assetMap, new object[] { guid });
                        if (fr2Asset == null) continue;

                        if (fr2AssetType == null)
                        {
                            fr2AssetType = fr2Asset.GetType();
                            findUsedByMethod = fr2AssetType.GetMethod("FindUsedBy",
                                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                        }
                        if (findUsedByMethod == null) continue;

                        var usedByList = findUsedByMethod.Invoke(null, new[] { fr2Asset }) as System.Collections.IList;
                        if (usedByList == null) continue;

                        foreach (var item in usedByList)
                        {
                            string refPath = GetStringMember(item, "assetPath");
                            if (!string.IsNullOrEmpty(refPath) && refPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                                result.Add(refPath);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (result.Count > 0)
                Debug.Log($"[ImageSimilarityPlugin] FR2 找到 {result.Count} 个引用 Prefab。");
            return new List<string>(result);
        }

        /// <summary>
        /// 使用 Unity 原生 API 全量扫描所有 Prefab 的依赖关系来查找引用。
        /// Prefab 数量较多时较慢，但不需要 FR2。
        /// </summary>
        private static List<string> FindWithNativeAPI(HashSet<string> oldAssetPaths,
            Action<int, int> progressCallback)
        {
            var result = new HashSet<string>();
            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            Debug.Log($"[ImageSimilarityPlugin] 扫描 {prefabGuids.Length} 个 Prefab 查找引用...");

            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                if (string.IsNullOrEmpty(prefabPath)) continue;

                progressCallback?.Invoke(i, prefabGuids.Length);

                string[] deps = AssetDatabase.GetDependencies(prefabPath, false);
                foreach (var dep in deps)
                    if (oldAssetPaths.Contains(dep)) { result.Add(prefabPath); break; }
            }

            Debug.Log($"[ImageSimilarityPlugin] 原生扫描完成，找到 {result.Count} 个引用 Prefab。");
            return new List<string>(result);
        }

        // ==================================================================
        //  FR2 反射底层
        // ==================================================================

        /// <summary>
        /// 在所有已加载程序集中按名称查找类型。
        /// 支持无命名空间 / 带 FR2. 前缀 / 带 FindReference2. 前缀三种形式，
        /// 最后遍历所有导出类型按简单名称兜底匹配。
        /// </summary>
        private static Type FindTypeInAllAssemblies(string typeName)
        {
            if (_typeCache.TryGetValue(typeName, out Type cachedType)) return cachedType;
            if (_missingTypes.Contains(typeName)) return null;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            string[] qualifiedNames =
            {
                typeName,
                "FR2." + typeName,
                "FindReference2." + typeName,
                "vietlabs.fr2." + typeName,
            };

            // 当前 FR2 使用 vietlabs.fr2 命名空间；先按完整名称查询，避免枚举大型程序集的全部类型。
            foreach (var asm in assemblies)
            {
                foreach (string qualifiedName in qualifiedNames)
                {
                    Type type = asm.GetType(qualifiedName);
                    if (type == null) continue;
                    _typeCache[typeName] = type;
                    return type;
                }
            }

            // 仅为未知旧版本保留一次简单名称兜底；命中或未命中结果都会缓存到当前 Domain 生命周期结束。
            foreach (var asm in assemblies)
            {
                try
                {
                    foreach (var candidate in asm.GetTypes())
                    {
                        if (candidate?.Name != typeName) continue;
                        _typeCache[typeName] = candidate;
                        return candidate;
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (var candidate in ex.Types)
                    {
                        if (candidate?.Name != typeName) continue;
                        _typeCache[typeName] = candidate;
                        return candidate;
                    }
                }
                catch { }
            }

            _missingTypes.Add(typeName);
            return null;
        }

        private static Type GetFR2FacadeType()
        {
            return FindTypeInAllAssemblies("FR2");
        }

        private static bool TryGetFR2FacadeReady(out bool ready)
        {
            ready = false;
            Type t = GetFR2FacadeType();
            if (t == null) return false;

            try
            {
                if (_fr2IsReadyProperty == null)
                    _fr2IsReadyProperty = t.GetProperty("IsReady", BindingFlags.Public | BindingFlags.Static);
                if (_fr2IsReadyProperty == null) return false;
                ready = (bool)_fr2IsReadyProperty.GetValue(null);
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetReferenceCountWithFR2Facade(string guid, out int count)
        {
            count = 0;
            Type t = GetFR2FacadeType();
            if (t == null) return false;

            try
            {
                if (_fr2GetUsedByCountMethod == null)
                    _fr2GetUsedByCountMethod = t.GetMethod(
                        "GetUsedByCount", BindingFlags.Public | BindingFlags.Static);
                if (_fr2GetUsedByCountMethod == null) return false;

                var result = _fr2GetUsedByCountMethod.Invoke(
                    null, new object[] { new[] { guid } }) as IDictionary;
                if (result == null) return false;
                if (result.Contains(guid))
                {
                    count = Convert.ToInt32(result[guid]);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static List<string> FindWithFR2Facade(HashSet<string> oldAssetPaths)
        {
            Type fr2Type = GetFR2FacadeType();
            if (fr2Type == null) return new List<string>();

            string[] guids = oldAssetPaths
                .Select(AssetDatabase.AssetPathToGUID)
                .Where(guid => !string.IsNullOrEmpty(guid))
                .ToArray();
            if (guids.Length == 0) return new List<string>();

            try
            {
                // 当前 FR2 公开 API 可返回直接引用资产；用反射避免 ImageSimilarityPlugin 强引用 FR2.asmdef。
                Type depType = FindTypeInAllAssemblies("Dependency");
                Type depthFilterType = FindTypeInAllAssemblies("DepthFilter");
                Type sortingType = FindTypeInAllAssemblies("Sorting");
                if (depType == null || depthFilterType == null || sortingType == null)
                    return new List<string>();

                var method = fr2Type.GetMethod("GetUsedBy", new[]
                {
                    typeof(string[]),
                    depType,
                    typeof(int),
                    depthFilterType,
                    sortingType
                });
                if (method == null) return new List<string>();

                var usedByList = method.Invoke(null, new[]
                {
                    guids,
                    Enum.Parse(depType, "Direct"),
                    (object)1,
                    Enum.Parse(depthFilterType, "Equal"),
                    Enum.Parse(sortingType, "None")
                }) as IEnumerable;
                if (usedByList == null) return new List<string>();

                var result = new HashSet<string>();
                foreach (var item in usedByList)
                {
                    string refPath = GetStringMember(item, "assetPath");
                    if (!string.IsNullOrEmpty(refPath) && refPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        result.Add(refPath);
                }

                if (result.Count > 0)
                    Debug.Log($"[ImageSimilarityPlugin] FR2 公开 API 找到 {result.Count} 个引用 Prefab。");
                return new List<string>(result);
            }
            catch { return new List<string>(); }
        }

        private static object GetMemberValue(object target, string memberName, BindingFlags flags)
        {
            if (target == null) return null;
            Type t = target.GetType();
            var prop = t.GetProperty(memberName, flags);
            if (prop != null) return prop.GetValue(target);
            var field = t.GetField(memberName, flags);
            return field?.GetValue(target);
        }

        private static string GetStringMember(object target, string memberName)
        {
            return GetMemberValue(target, memberName,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) as string;
        }

        private static object GetStaticMemberValue(Type type, string memberName)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var prop = type.GetProperty(memberName, flags);
            if (prop != null) return prop.GetValue(null);
            var field = type.GetField(memberName, flags);
            return field?.GetValue(null);
        }

        private static string GetStaticMemberString(Type type, string memberName)
        {
            return GetStaticMemberValue(type, memberName)?.ToString();
        }

        private static bool GetStaticMemberBool(Type type, string memberName)
        {
            object value = GetStaticMemberValue(type, memberName);
            if (value == null) return false;
            try { return Convert.ToBoolean(value); }
            catch { return false; }
        }

        private static bool CacheStatusIndicatesExistingCache(string cacheStatus)
        {
            if (string.IsNullOrEmpty(cacheStatus)) return false;
            return !string.Equals(cacheStatus, "None", StringComparison.Ordinal)
                && !string.Equals(cacheStatus, "NotExist", StringComparison.Ordinal);
        }

        private static void FillStatusText(FR2StatusSnapshot snapshot)
        {
            if (!snapshot.Installed)
            {
                snapshot.Label = " FR2 未安装";
                snapshot.Tooltip = "未检测到 FR2 程序集。";
                return;
            }

            if (snapshot.Ready && snapshot.HasPendingChanges)
                snapshot.Label = " FR2 缓存待刷新";
            else if (snapshot.Ready)
                snapshot.Label = " FR2 已就绪";
            else if (string.Equals(snapshot.Status, "Wait4Refresh", StringComparison.Ordinal)
                || string.Equals(snapshot.CacheStatus, "Incompatible", StringComparison.Ordinal))
                snapshot.Label = " FR2 缓存待刷新";
            else if (string.Equals(snapshot.Status, "RefreshDB", StringComparison.Ordinal)
                || string.Equals(snapshot.Status, "ReadAsset", StringComparison.Ordinal)
                || string.Equals(snapshot.Status, "BuildUsedByMap", StringComparison.Ordinal))
                snapshot.Label = " FR2 扫描中";
            else if (snapshot.HasCacheAsset || snapshot.HasCacheData)
                snapshot.Label = " FR2 初始化中";
            else
                snapshot.Label = " FR2 缓存为空";

            string status = string.IsNullOrEmpty(snapshot.Status) ? "-" : snapshot.Status;
            string cacheStatus = string.IsNullOrEmpty(snapshot.CacheStatus) ? "-" : snapshot.CacheStatus;
            snapshot.Tooltip = $"status={status}\ncacheStatus={cacheStatus}\n" +
                               $"AssetMap={snapshot.AssetMapCount}, AssetList={snapshot.AssetListCount}, " +
                               $"Timestamp={snapshot.CacheTimestamp}, Pending={snapshot.HasPendingChanges}";
        }

        /// <summary>
        /// 通过反射获取 FR2_Cache 单例。
        /// 兼容旧版 FR2_Cache.Api 和当前 FR2_Cache._inst。
        /// </summary>
        private static object GetFR2Cache() => GetFR2Cache(out _);

        private static object GetFR2Cache(out bool hasCacheAsset)
        {
            hasCacheAsset = false;
            Type t = FindTypeInAllAssemblies("FR2_Cache");
            if (t == null) return null;
            var apiProp = t.GetProperty("Api", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (apiProp != null)
            {
                object api = apiProp.GetValue(null);
                if (api != null)
                {
                    hasCacheAsset = true;
                    return api;
                }
            }

            var instField = t.GetField("_inst", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            object inst = instField?.GetValue(null);
            if (inst != null)
            {
                hasCacheAsset = true;
                return inst;
            }

            // FR2 初始化是 delayCall 驱动；_inst 还没赋值时，直接读取磁盘上的缓存资产判断是否已有缓存。
            return FindFR2CacheAsset(t, out hasCacheAsset);
        }

        private static object FindFR2CacheAsset(Type cacheType, out bool hasCacheAsset)
        {
            if (_fr2CacheAssetLookupCompleted)
            {
                if (_fr2CacheAsset is UnityEngine.Object unityObject && unityObject == null)
                {
                    _fr2CacheAsset = null;
                    _fr2CacheAssetExists = false;
                    _fr2CacheAssetLookupCompleted = false;
                }
                else
                {
                    hasCacheAsset = _fr2CacheAssetExists;
                    return _fr2CacheAsset;
                }
            }

            _fr2CacheAssetLookupCompleted = true;
            _fr2CacheAssetExists = false;
            string[] cacheGuids = AssetDatabase.FindAssets("t:fr2_cache");
            foreach (string cacheGuid in cacheGuids)
            {
                _fr2CacheAssetExists = true;
                string cachePath = AssetDatabase.GUIDToAssetPath(cacheGuid);
                if (string.IsNullOrEmpty(cachePath)) continue;
                var cacheAsset = AssetDatabase.LoadAssetAtPath(cachePath, cacheType);
                if (cacheAsset == null) continue;
                _fr2CacheAsset = cacheAsset;
                hasCacheAsset = true;
                return cacheAsset;
            }

            hasCacheAsset = _fr2CacheAssetExists;
            return null;
        }

        /// <summary>
        /// 从 FR2 缓存中取 AssetMap（Dictionary&lt;string, FR2_Asset&gt;）。
        /// AssetMap 以 GUID 为键，存储每个资产的依赖信息。
        /// </summary>
        private static object GetFR2AssetMap(object cache)
        {
            if (cache == null) return null;

            var staticMap = cache.GetType().GetField("_map",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (staticMap != null) return staticMap.GetValue(null);

            var field = cache.GetType().GetField("AssetMap",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(cache);
        }

        private static object GetFR2AssetList(object cache)
        {
            if (cache == null) return null;
            return cache.GetType()
                .GetField("_assets", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(cache);
        }

        private static int GetCollectionCount(object collection)
        {
            if (collection == null) return 0;
            var countProp = collection.GetType().GetProperty("Count");
            if (countProp == null) return 0;
            return Convert.ToInt32(countProp.GetValue(collection));
        }

        private static int GetIntMember(object target, string memberName)
        {
            object value = GetMemberValue(target, memberName,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (value == null) return 0;
            try { return Convert.ToInt32(value); }
            catch { return 0; }
        }
    }
}
