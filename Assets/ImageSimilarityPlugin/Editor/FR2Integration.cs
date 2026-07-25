using System;
using System.Collections.Generic;
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
        private static Dictionary<string, int> _refCountCache = new Dictionary<string, int>();

        // ==================================================================
        //  FR2 检测
        // ==================================================================

        /// <summary>
        /// FR2 是否已安装并且 API 可用。
        /// 通过反射查找 FR2_Cache 类型，验证 Api 和 AssetMap 可访问。
        /// </summary>
        public static bool HasFR2()
        {
            if (_fr2Available.HasValue) return _fr2Available.Value;

            try
            {
                object cache = GetFR2Cache();
                object assetMap = GetFR2AssetMap(cache);
                _fr2Available = assetMap != null;
                Debug.Log($"[ImageSimilarityPlugin] FR2 {(_fr2Available.Value ? "已检测到" : "未检测到")}。");
            }
            catch
            {
                _fr2Available = false;
            }
            return _fr2Available.Value;
        }

        /// <summary>
        /// FR2 是否已就绪（已安装且 AssetMap 中有缓存数据）。
        /// AssetMap 为空通常表示 FR2 还未执行过项目扫描。
        /// </summary>
        public static bool IsReady
        {
            get
            {
                if (!HasFR2()) return false;
                try
                {
                    object assetMap = GetFR2AssetMap(GetFR2Cache());
                    var countProp = assetMap?.GetType().GetProperty("Count");
                    return (int)(countProp?.GetValue(assetMap) ?? 0) > 0;
                }
                catch { return false; }
            }
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
            if (!HasFR2()) return 0;

            string assetPath = PluginUtils.AbsoluteToAssetPath(absolutePath);
            if (string.IsNullOrEmpty(assetPath)) return 0;
            if (_refCountCache.TryGetValue(assetPath, out int cached)) return cached;

            int count = 0;
            try
            {
                object assetMap = GetFR2AssetMap(GetFR2Cache());
                if (assetMap == null) { _refCountCache[assetPath] = 0; return 0; }

                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid)) { _refCountCache[assetPath] = 0; return 0; }

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
                    var usedByMap = fr2Asset.GetType().GetField("UsedByMap",
                        BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(fr2Asset);
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

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 10,
                normal = { textColor = Color.white }
            };
            GUI.color = Color.white;
            GUI.Label(badgeRect, count > 99 ? "99+" : count.ToString(), labelStyle);
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

                        var pathField = fr2AssetType.GetField("assetPath", BindingFlags.Public | BindingFlags.Instance);
                        foreach (var item in usedByList)
                        {
                            string refPath = pathField?.GetValue(item) as string;
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
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName)
                     ?? asm.GetType("FR2." + typeName)
                     ?? asm.GetType("FindReference2." + typeName);
                if (t != null) return t;

                try
                {
                    foreach (var exported in asm.GetExportedTypes())
                        if (exported.Name == typeName) return exported;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 通过反射获取 FR2_Cache 单例。
        /// 访问 FR2_Cache.Api 属性。
        /// </summary>
        private static object GetFR2Cache()
        {
            Type t = FindTypeInAllAssemblies("FR2_Cache");
            if (t == null) return null;
            var apiProp = t.GetProperty("Api", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            return apiProp?.GetValue(null);
        }

        /// <summary>
        /// 从 FR2 缓存中取 AssetMap（Dictionary&lt;string, FR2_Asset&gt;）。
        /// AssetMap 以 GUID 为键，存储每个资产的依赖信息。
        /// </summary>
        private static object GetFR2AssetMap(object cache)
        {
            if (cache == null) return null;
            var field = cache.GetType().GetField("AssetMap",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            return field?.GetValue(cache);
        }
    }
}
