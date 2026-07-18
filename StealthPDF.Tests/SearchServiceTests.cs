using StealthPDF.Services;
using Xunit;

namespace StealthPDF.Tests
{
    public class SearchServiceTests
    {
        private readonly SearchService _svc = new();

        [Fact]
        public void Search_EmptyQuery_ReturnsEmpty()
        {
            var result = _svc.Search("irrelevant.pdf", "");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_WhitespaceQuery_ReturnsEmpty()
        {
            var result = _svc.Search("irrelevant.pdf", "   ");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_EmptyFilePath_ReturnsEmpty()
        {
            var result = _svc.Search("", "hello");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_MissingFile_ReturnsEmpty()
        {
            // Should not throw; non-existent file produces no results.
            var result = _svc.Search(@"C:\does\not\exist.pdf", "hello");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void SearchResult_PageRects_EmptyByDefault()
        {
            var result = new SearchResult();
            Assert.Empty(result.PageRects);
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }
    }
}
