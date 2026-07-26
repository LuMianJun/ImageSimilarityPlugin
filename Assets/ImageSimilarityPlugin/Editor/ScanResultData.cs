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
    /// 以图搜图查询结果，对应 image_query_cli.py 输出的 JSON。
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
