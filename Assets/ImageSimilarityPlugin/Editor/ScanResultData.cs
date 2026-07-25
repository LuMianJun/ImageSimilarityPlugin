using System;
using System.Collections.Generic;

namespace ImageSimilarityPlugin
{
    /// <summary>
    /// Top-level result from the Python CLI JSON output.
    /// </summary>
    [Serializable]
    public class ScanResultData
    {
        public int total_images;
        public int total_groups;
        public List<DuplicateGroup> groups;
        public double elapsed_seconds;
    }

    /// <summary>
    /// A group of similar/duplicate images.
    /// </summary>
    [Serializable]
    public class DuplicateGroup
    {
        public int id;
        public List<string> images;
    }
}
