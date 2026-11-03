using System;
using System.Collections.Generic;

namespace TDU2.Online
{
    /// <summary>Umfrage vom Server (op "surveys"/"vote"; im Web-Panel angelegt). Feldnamen = Server Data/SurveyStore.cs SurveyEntry.</summary>
    [Serializable]
    public sealed class SurveyEntry
    {
        public int id;
        public string question = "";
        public string text = "";
        public List<string> answers = new List<string>();
        public List<int> counts = new List<int>();
        public int total;
        public int myVote = -1;       // -1 = noch nicht abgestimmt
        public bool open;
        public string created = "";   // ISO (UTC)
        public string closes = "";    // ISO (UTC), leer = kein Ende
    }
}
