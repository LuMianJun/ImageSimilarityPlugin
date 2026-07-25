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
}
