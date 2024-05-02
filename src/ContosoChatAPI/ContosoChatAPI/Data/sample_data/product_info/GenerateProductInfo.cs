using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

namespace ContosoChatAPI.Data
{
    public class GenerateProductInfo
    {
        private ILogger<GenerateProductInfo> _logger;
        private IConfiguration _config;
        private readonly OpenAIClient _openAIClient;

        private readonly SearchClient _searchClient;
        private readonly SearchIndexClient _searchIndexClient;
        readonly string _indexName;

        public GenerateProductInfo(ILogger<GenerateProductInfo> logger, IConfiguration config, OpenAIClient openAIClient, SearchClient searchClient, SearchIndexClient searchIndexClient)
        {
            _logger = logger;
            _config = config;
            _openAIClient = openAIClient;
            _searchClient = searchClient;
            _searchIndexClient = searchIndexClient;
            _indexName = config["AzureAISearch:index_name"];
        }

        public async Task PopulateSearchIndexAsync()
        {
            var indexExists = false;

            try
            {
                _logger.LogInformation("Verifying if AI Search index exists...");
                var indexResponse = await _searchIndexClient.GetIndexAsync(_indexName);

                if (indexResponse.Value.Name == _indexName)
                {
                    indexExists = true;
                    // Check if there are any documents in the index
                    var searchOptions = new SearchOptions
                    {
                        Size = 1,
                        IncludeTotalCount = true
                    };
                    var searchResults = await _searchClient.SearchAsync<Dictionary<string, object>>("*", searchOptions);
                    if (searchResults.Value.TotalCount > 0)
                    {
                        // Index already has documents, nothing to do
                        _logger.LogInformation("AI Search index already exists, nothing to do.");
                        return;
                    }
                }
            }
            catch (RequestFailedException)
            {
                //Nothing to do, this is expected because index doesn't exists
            }

            _logger.LogInformation("AI Search index not found, creating index...");

            // Create the index
            var index = new SearchIndex(_indexName)
            {
                Fields =
                {
                    new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true },
                    new SearchableField("content") { IsFilterable = true, IsSortable = true },
                    new SimpleField("filepath", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
                    new SearchableField("title") { IsFilterable = true, IsSortable = true },
                    new SimpleField("url", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
                    new VectorSearchField("contentVector", 1536, "myHnswProfile")
                },
                SemanticSearch = new()
                {
                    Configurations =
                {
                    new SemanticConfiguration("default", new()
                    {
                        TitleField = new SemanticField("title"),
                        ContentFields =
                        {
                            new SemanticField("content")
                        }
                    })
                }
                },
                VectorSearch = new()
                {
                    Algorithms =
                {
                    new HnswAlgorithmConfiguration("myHnsw"),
                    new ExhaustiveKnnAlgorithmConfiguration("myExhaustiveKnn")
                },
                    Profiles =
                {
                    new VectorSearchProfile("myHnswProfile", "myHnsw"),
                    new VectorSearchProfile("myExhaustiveKnnProfile", "myExhaustiveKnn")
                }
                }
            };

            try
            {
                if (!indexExists)
                {
                    await _searchIndexClient.CreateIndexAsync(index);
                }

                // Index the documents
                // Load products from CSV file
                var products = File.ReadAllLines("./Data/sample_data/product_info/products.csv").Select(line => line.Split(',')).Select(values => new
                {
                    id = values[0],
                    name = values[1],
                    description = values[2]
                }).ToList();

                // Generate documents
                var documents = new List<Dictionary<string, object>>();
                foreach (var product in products)
                {
                    var content = product.description;
                    var id = product.id;
                    var title = product.name;
                    var url = $"/products/{title.ToLower().Replace(' ', '-')}";

                    EmbeddingsOptions embeddingOptions = new()
                    {
                        DeploymentName = _config["OpenAi:embedding_deployment"],
                        Input = { content },
                    };

                    var returnValue = await _openAIClient.GetEmbeddingsAsync(embeddingOptions);
                    var embedding = returnValue.Value.Data[0].Embedding.ToArray();
                    var document = new Dictionary<string, object>
                    {
                        { "id", id },
                        { "content", content },
                        { "filepath", title.ToLower().Replace(' ', '-') },
                        { "title", title },
                        { "url", url },
                        { "contentVector", embedding }
                    };
                    documents.Add(document);
                }

                // Index the documents
                List<IndexDocumentsAction<Dictionary<string, object>>> actions = documents.Select(doc => IndexDocumentsAction.Upload(doc)).ToList();
                var batch = IndexDocumentsBatch.Create(actions.ToArray());
                await _searchClient.IndexDocumentsAsync(batch);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error in AI Search: " + ex.Message);
            }
        }
    }
}