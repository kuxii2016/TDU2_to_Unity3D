using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    [Serializable]
    public sealed class BookmarkEntry
    {
        public int id;
        public string island = "";
        public float x, y, z;
    }
}
