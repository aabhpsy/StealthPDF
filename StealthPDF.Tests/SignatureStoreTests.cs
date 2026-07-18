using StealthPDF.Services;
using Xunit;

namespace StealthPDF.Tests
{
    public class SignatureStoreTests
    {
        [Fact]
        public void NewStore_HasEmptySignatures()
        {
            var store = new SignatureStore();
            Assert.Empty(store.Signatures);
        }

        [Fact]
        public void Add_IncreasesCount()
        {
            var store = new SignatureStore();
            store.Add(new SavedSignature { Name = "Test" });
            Assert.Single(store.Signatures);
        }

        [Fact]
        public void Remove_DecreasesCount()
        {
            var store = new SignatureStore();
            var sig = new SavedSignature { Name = "Test" };
            store.Add(sig);
            store.Remove(sig);
            Assert.Empty(store.Signatures);
        }

        [Fact]
        public void RoundTrip_PersistAndLoad()
        {
            var dir  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "sig_test.json");

            try
            {
                var store1 = new SignatureStore(dir, file);
                store1.Add(new SavedSignature { Name = "Alpha", CanvasWidth = 400, CanvasHeight = 150 });
                store1.Add(new SavedSignature { Name = "Beta",  CanvasWidth = 300, CanvasHeight = 100 });
                store1.Persist();

                var store2 = new SignatureStore(dir, file);
                store2.Load();

                Assert.Equal(2, store2.Signatures.Count);
                Assert.Equal("Alpha", store2.Signatures[0].Name);
                Assert.Equal("Beta",  store2.Signatures[1].Name);
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }
}
