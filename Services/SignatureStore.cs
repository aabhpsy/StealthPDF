using System.Text.Json;

namespace StealthPDF.Services
{
    internal sealed class SignatureStore
    {
        private readonly string _dir;
        private readonly string _file;

        private static readonly string DefaultDir  = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StealthPDF");

        public SignatureStore()
            : this(DefaultDir, System.IO.Path.Combine(DefaultDir, "signatures.json")) { }

        internal SignatureStore(string dir, string file)
        {
            _dir  = dir;
            _file = file;
        }

        private List<SavedSignature> _items = [];

        public IReadOnlyList<SavedSignature> Signatures => _items;

        public void Load()
        {
            try
            {
                if (System.IO.File.Exists(_file))
                {
                    var json = System.IO.File.ReadAllText(_file);
                    _items = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? [];
                }
            }
            catch { _items = []; }
        }

        public void Persist()
        {
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(_file, json);
            }
            catch { /* best effort */ }
        }

        public void Add(SavedSignature sig) => _items.Add(sig);

        public void Remove(SavedSignature sig) => _items.Remove(sig);
    }
}
