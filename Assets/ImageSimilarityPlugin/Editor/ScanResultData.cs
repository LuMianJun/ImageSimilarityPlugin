using System;
using System.Collections.Generic;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// 扫描结果的数据结构，对应 Python CLI 输出的 JSON。
    /// 由 JsonUtility.FromJson 反序列化填充。
    /// </summary>
    [Serializable]
    public class ScanResultData
    {
        /// <summary>成功处理的图片总数</summary>
        public int total_images;

        /// <summary>相似图片组数</summary>
        public int total_groups;

        /// <summary>相似图片分组列表</summary>
        public List<DuplicateGroup> groups;

        /// <summary>扫描耗时（秒）</summary>
        public double elapsed_seconds;

        /// <summary>读取或特征提取失败、已跳过的图片路径</summary>
        public List<string> failed_images;

        /// <summary>特征缓存更新信息（持久会话命中时有值）</summary>
        public CacheInfo cache_info;
    }

    /// <summary>
    /// 一组相似/重复图片。
    /// </summary>
    [Serializable]
    public class DuplicateGroup
    {
        /// <summary>组编号（从 1 开始）</summary>
        public int id;

        /// <summary>组内所有图片的绝对路径列表</summary>
        public List<string> images;
    }

    /// <summary>
    /// 以图搜图查询结果，对应 image_query_cli.py / query_server.py 输出的 JSON。
    /// 由 JsonUtility.FromJson 反序列化填充。
    /// </summary>
    [Serializable]
    public class QueryResultData
    {
        /// <summary>目标文件夹中成功处理的图片总数</summary>
        public int total_images;

        /// <summary>查询图片的绝对路径</summary>
        public string query_image;

        /// <summary>使用的相似度阈值</summary>
        public float threshold;

        /// <summary>按相似度降序排列的结果列表</summary>
        public List<SimilarImage> results;

        /// <summary>查询耗时（秒）</summary>
        public double elapsed_seconds;

        /// <summary>读取或特征提取失败、已跳过的图片路径</summary>
        public List<string> failed_images;

        /// <summary>特征缓存更新信息（持久会话命中时有值，子进程回退时可能为 null）</summary>
        public CacheInfo cache_info;
    }

    /// <summary>
    /// 特征缓存状态信息，由 query_server.py 返回。
    ///
    /// 用于两种场景：
    /// 1. scan/query 完成后 — 报告本次增量更新了多少张
    /// 2. check_cache 预先检查 — 报告缓存中有多少张过期/新增/删除
    /// </summary>
    [Serializable]
    public class CacheInfo
    {
        // ---- 增量更新后字段 (scan/query result) ----
        /// <summary>缓存是否命中</summary>
        public bool cache_hit;

        /// <summary>直接复用缓存的图片数（mtime 未变）</summary>
        public int fresh_used;

        /// <summary>mtime 变化，本次重新提取的数量</summary>
        public int re_extracted;

        /// <summary>缓存中不存在，本次新增提取的数量</summary>
        public int new_added;

        /// <summary>文件已删除，从缓存移除的数量</summary>
        public int missing_removed;

        // ---- 预检字段 (check_cache result) ----
        /// <summary>mtime 变化、尚未更新的数量（check_cache 时）</summary>
        public int stale_count;

        /// <summary>缓存中有但文件已不存在的数量（check_cache 时）</summary>
        public int missing_count;

        /// <summary>mtime 未变的图片数（check_cache 时）</summary>
        public int fresh_count;

        /// <summary>文件夹中有但缓存中没有的数量（check_cache 时）</summary>
        public int new_since_cache;

        /// <summary>当前文件夹中的图片总数</summary>
        public int total_current;

        // ---- 通用 ----
        /// <summary>缓存中的图片总数</summary>
        public int total_cached;

        /// <summary>缓存是否有需要关注的变化（过期/新增/删除任一项>0）</summary>
        public bool HasChanges =>
            stale_count > 0 || new_since_cache > 0 || missing_count > 0 ||
            re_extracted > 0 || new_added > 0 || missing_removed > 0;
    }

    /// <summary>
    /// 单条相似图片结果，包含路径、相似度分数和排名。
    /// 字段名与 Python CLI 输出的 JSON 键名精确匹配。
    /// </summary>
    [Serializable]
    public class SimilarImage
    {
        /// <summary>相似图片的绝对路径</summary>
        public string image_path;

        /// <summary>余弦相似度 (0~1)</summary>
        public float similarity;

        /// <summary>排名（从 1 开始）</summary>
        public int rank;
    }
}
