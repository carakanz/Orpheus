using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Orpheus.Server
{
    /// <summary>Отчёт сервера (<c>--report</c>, 0-030): итог запуска и все окна метрик.</summary>
    public sealed class ServerReport
    {
        public string Mode { get; set; } = "";
        public int Size { get; set; }
        public string Bots { get; set; } = "";
        public int Port { get; set; } = -1;
        public string Machine { get; set; } = "";
        public string Configuration { get; set; } = "";

        /// <summary>От запуска процесса до строки готовности (порт слушается), с; без сети — до начала цикла тактов.</summary>
        public double ReadySeconds { get; set; }

        /// <summary>Рабочий набор и пик рабочего набора в миг готовности, МБ.</summary>
        public double ReadyWorkingSetMb { get; set; }
        public double ReadyPeakWorkingSetMb { get; set; }
        public double ReadyPrivateMb { get; set; }

        public MetricsSample Total { get; set; } = new MetricsSample();
        public List<MetricsSample> Windows { get; set; } = new List<MetricsSample>();

        public void Write(string path)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
