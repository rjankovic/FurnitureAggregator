using FA.Common;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace FA.Extractor
{
    class Program
    {
        private static string _currentDirectory = null;
        private static DataTable _filesTable = null;
        private static int _fileCounter = 0;
        private static string _site = null;
        private static int _extractId;
        private static NetBridge _db;

        static void Main(string[] args)
        {
            var tempDir = "C:\\TEMP";
            _currentDirectory = tempDir;
            _filesTable = new DataTable();
            _filesTable.Columns.Add("Path");
            _filesTable.Columns.Add("Url");
            _filesTable.Columns.Add("Name");
            _db = new NetBridge("Server=localhost;Initial Catalog=FurnitureAggregator;MultipleActiveResultSets=True;Integrated Security=true");

            CreateDirectory("FurnitureAggregator");
            ExtractSconto();

            WriteToCsv(_filesTable, Path.Combine(_currentDirectory, "files.csv"));
        }

        private static void ExtractSconto()
        {

            CreateDirectory("Sconto");
            _extractId = int.Parse(_db.ExecuteProcedureScalar("stg.CreateExtract", new Dictionary<string, object>() { { "type", "Sconto" } }).ToString());
            ExtractScontoNabytek();
            _db.ExecuteProcedure("stg.SetExtractEndStatus", new Dictionary<string, object>() { { "extractId", _extractId }, { "status", "DONE" } });
            GoToParentDirectory();
        }

        private static void ExtractScontoNabytek()
        {
            CreateDirectory("Nabytek");
            _site = "https://www.sconto.cz";
            var nabytekUrl = _site + "/nabytek";
            var nabytekHtml = DownloadAndSaveHtml(nabytekUrl, "Nábytek", "CategriesRoot", _site);
            var categoryHrefs = nabytekHtml.DocumentNode.SelectNodes("//span[@class='flyoutTile__Headline flyoutTile__Headline']/a");
            var categoryHrefsFiltered = categoryHrefs.Where(x => !x.InnerText.Contains("Matrace") && !x.InnerText.Contains("Rošty")).ToList();

            foreach (var node in categoryHrefsFiltered)
            {
                var name = node.InnerText;
                var href = node.Attributes["href"].Value;
                ExtractScontoCategory(name, href, nabytekUrl);
            }

            GoToParentDirectory();
        }

        private static void ExtractScontoCategory(string name, string url, string originUrl)
        {
            CreateDirectory(name);
            var categoryUrl = _site + url;
            var categoryHtml = DownloadAndSaveHtml(categoryUrl, name, "Category", originUrl);
            var secondLevelHrefs = categoryHtml.DocumentNode.SelectNodes("//a[@class='sidebarNavigation__secondLevelCategoryName']");

            foreach (var node in secondLevelHrefs)
            {
                var secondLevelName = node.InnerText;
                var href = node.Attributes["href"].Value;
                ExtractScontoSecondLevelCategory(secondLevelName, href, href);
            }

            GoToParentDirectory();
        }

        private static void ExtractScontoSecondLevelCategory(string name, string url, string originUrl)
        {
            CreateDirectory(name);
            var secondLevelUrl = _site + url;
            var secondLevelHtml = DownloadAndSaveHtml(secondLevelUrl, name, "Subcategory", originUrl);
            var artikelHrefs = secondLevelHtml.DocumentNode.SelectNodes("//div[@class='articleTile__info']/a[@class='articleInfo']");
            
            if (artikelHrefs == null)
            {
                Console.WriteLine(string.Format("! Skipping {0} - no artikel hrefs", url));
                GoToParentDirectory();
                return;
            }
            
            foreach (var artikelHref in artikelHrefs)
            {
                var href = artikelHref.Attributes["href"].Value;
                var nameSpan = artikelHref.ChildNodes.First().ChildNodes.FirstOrDefault(x => x.HasClass("articleInfo__name"));
                if (nameSpan == null)
                {
                    nameSpan = artikelHref.ChildNodes.First().ChildNodes.FirstOrDefault(x => x.HasClass("articleInfo__specification"));
                }
                if (nameSpan == null)
                {
                    Console.WriteLine(string.Format("! Skipping {0} - no artikel name", href));
                    continue;
                }
                var artikelName = nameSpan.InnerText;
                ExtractScontoArtikel(artikelName, href, originUrl);
            }

            GoToParentDirectory();
        }

        private static void ExtractScontoArtikel(string name, string url, string originUrl)
        {
            var artikelUrl = _site + url;
            var artikelHtml = DownloadAndSaveHtml(artikelUrl, name, "Artikel", originUrl, out int extractItemId);

            DataTable attributesTable = new DataTable("[Stg].[UDTT_ExtractItemAttributes]");
            attributesTable.Columns.Add(new DataColumn("AttributeCategory"));
            attributesTable.Columns.Add(new DataColumn("AttributeName"));
            attributesTable.Columns.Add(new DataColumn("AttributeValue"));

            var articleInfoBlock = artikelHtml.DocumentNode.SelectSingleNode("//div[@id='articleInfoBlock']");
            ExtractScontoArtikelInfoBlock(articleInfoBlock, attributesTable);

            _db.ExecuteProcedure("[Stg].[InsertExtractItemAttributes]", new Dictionary<string, object>()
            {
                { "extractItemId", extractItemId },
                { "attributes", attributesTable }
            });
        }

        private static void ExtractScontoArtikelInfoBlock(HtmlNode infoBlock, DataTable attributesTable)
        {
            //<div class="articleFullName articleFullName__articleInformationInfoBlock"><span class="articleFullName__specification">Sedací souprava</span><span class="articleFullName__name"> LIMONE</span></div>
            var fullName = infoBlock.SelectSingleNode("//div[@id='articleInfoBlock']//div[@class='articleFullName articleFullName__articleInformationInfoBlock']");
            foreach (var fullNamePart in fullName.ChildNodes.Where(x => x.Name == "span"))
            {
                var attributeName = fullNamePart.Attributes["class"].Value;
                var attributeValue = fullNamePart.InnerHtml;
                AddAttributesTableRow(attributesTable, "FullName", attributeName, attributeValue);
            }

            //<span class="bulletList__configurationTitle">Provedení: <span class="bulletList__configurationText">doprava</span></span>
            var configurationItems = infoBlock.SelectNodes("//div[@id='articleInfoBlock']//span[@class='bulletList__configurationTitle']");
            if (configurationItems != null)
            {
                foreach (var configurationItem in configurationItems)
                {
                    var attributeName = configurationItem.InnerText.Trim();
                    var attributeValue = configurationItem.ChildNodes.First(x => x.Name == "span").InnerText.Trim();
                    AddAttributesTableRow(attributesTable, "Configuration", attributeName, attributeValue);
                }
            }

            var articleInformationInner = infoBlock.ChildNodes.First(x => x.HasClass("articleInformation"));
            var articleNr = articleInformationInner.ChildNodes.First(x => x.HasClass("articleInformation__articleNr")).InnerText;
            var articleNrCleared = articleNr.Substring(articleNr.LastIndexOf(':') + 1).Trim();
            AddAttributesTableRow(attributesTable, "ArticleNr", "ArticleNr", articleNrCleared);
            //<div class="articleInformation__articleNr">Číslo produktu: :  412020100</div>

            var price = articleInformationInner.ChildNodes.First(x => x.HasClass("articleInformation__price"));
            var priceTextADS = price.ChildNodes.First(x => x.HasClass("articlePrices__text--ADS"));
            var priceADS = price.ChildNodes.First(x => x.HasClass("articlePrices--ADS"));
            var prices = priceADS.ChildNodes.Where(x => x.HasClass("articlePrices__price")).ToList();
            foreach (var priceItem in prices)
            {
                var priceTitle = priceItem.ChildNodes.FirstOrDefault(x => x.HasClass("articlePrices__priceTitle"));
                
                //optional <div class="articlePrice__onSaleContainer">
                var priceValue = priceItem.ChildNodes.First(x => x.HasClass("articlePrice"));
                var priceInteger = priceValue.Descendants().First(x => x.HasClass("articlePrice__integer")).InnerHtml;
                var priceIntegerTrim = priceInteger.Replace("&nbsp;", "").Trim();

                if (priceTitle != null)
                {
                    var priceTitleText = priceTitle.InnerText.Trim();
                    AddAttributesTableRow(attributesTable, "Price", priceTitleText, priceIntegerTrim);
                }
                else
                {
                    AddAttributesTableRow(attributesTable, "Price", "Price", priceIntegerTrim);
                }
            }

            var priceSmallText = priceTextADS.ChildNodes.First(x => x.HasClass("articlePrices__smallText"));
            var priceSmallTextText = priceSmallText.InnerText;
            foreach (var childNode in priceSmallText.ChildNodes)
            {
                priceSmallTextText += childNode.InnerText;
            }
            AddAttributesTableRow(attributesTable, "Price", "PriceNote", priceSmallTextText);
            
            //<div class="articlePrices__text articlePrices__text--ADS" id="articlePresentationServiceOfferText"><p class="articlePrices__smallText">Včetně DPH bez <a id="deliveryInformation" data-article-number="412020100">ceny dopravy</a></p></div>

            //<div class="articleInformation__price"><div class="articlePrices articlePrices--ADS">
            //< div class="articlePrices__price articlePrices__price--oldPriceADS"><div class="articlePrices__priceTitle articlePrices__priceTitle--addedTextADS">Původní cena</div><del class="articlePrice articlePrice--oldPriceADS"><span class="articlePrice__integer">4&nbsp;999</span><span class="articlePrice__dsep"></span><span class="articlePrice__fraction articlePrice__fraction"></span><span class="articlePrice__currency articlePrice__currency"> Kč</span></del></div><div class="articlePrices__price articlePrices__price--deltaOnlinePriceADS"><div class="articlePrices__priceTitle articlePrices__priceTitle--colorMain">Ušetříte</div><span class="articlePrice articlePrice--deltaOnlinePriceADS"><span class="articlePrice__integer">2&nbsp;000</span><span class="articlePrice__dsep"></span><span class="articlePrice__fraction articlePrice__fraction"></span><span class="articlePrice__currency articlePrice__currency"> Kč</span></span></div><div class="articlePrices__price articlePrices__price--onSaleADS"><div class="articlePrices__priceTitle articlePrices__priceTitle--center">Akční cena</div><span class="articlePrice articlePrice--onSaleADS"><div class="articlePrice__onSaleContainer"><span class="articlePrice__integer">2&nbsp;999</span><span class="articlePrice__dsep"></span><span class="articlePrice__fraction articlePrice__fraction"></span><span class="articlePrice__currency articlePrice__currency"> Kč</span></div></span></div></div><div class="articlePrices__text articlePrices__text--ADS" id="articlePresentationServiceOfferText"><p class="articlePrices__smallText">Včetně DPH bez <a id="deliveryInformation" data-article-number="412020100">ceny dopravy</a></p></div></div>
        }

        private static void AddAttributesTableRow(DataTable attributesTable, string category, string name, string value)
        {
            var nr = attributesTable.NewRow();
            nr[0] = category;
            nr[1] = name;
            nr[2] = value;
            attributesTable.Rows.Add(nr);
        }


        private static void CreateDirectory(string directoryName)
        {
            var directoryNameSanitized = directoryName;
            string invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());

            foreach (char c in invalid)
            {
                directoryNameSanitized = directoryNameSanitized.Replace(c.ToString(), "");
            }

            var newPath = Path.Combine(_currentDirectory, directoryNameSanitized);
            if (Directory.Exists(newPath))
            {
                Directory.Delete(newPath, true);
            }
            Directory.CreateDirectory(newPath);
            _currentDirectory = newPath;
        }

        private static void GoToParentDirectory()
        {
            var parentPath = Directory.GetParent(_currentDirectory);
            _currentDirectory = parentPath.FullName;
        }

        private static string DownloadHtml(string url)
        {
            WebClient wc = new WebClient();
            wc.Encoding = System.Text.Encoding.UTF8;
            var res = wc.DownloadString(url);
            return res;
        }

        private static HtmlDocument DownloadAndSaveHtml(string url, string name, string pageType, string originUrl)
        {
            return DownloadAndSaveHtml(url, name, pageType, originUrl, out int dummy);
        }

        private static HtmlDocument DownloadAndSaveHtml(string url, string name, string pageType, string originUrl, out int insertedItemId)
        {
            Console.WriteLine(url + "\t(" + name + ")");
            var html = DownloadHtml(url);
            var fileName = "File_" + _fileCounter++.ToString() + ".html";
            var filePath = Path.Combine(_currentDirectory, fileName);
            File.WriteAllText(filePath, html);
            var nr = _filesTable.NewRow();
            nr[0] = filePath;
            nr[1] = url;
            nr[2] = name;
            _filesTable.Rows.Add(nr);

            
            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(html);
            //var body = doc.DocumentNode.SelectSingleNode("//body");

            //doc.OptionOutputAsXml = true;
            
            //StringBuilder sb = new StringBuilder();
            //StringWriter sw = new StringWriter(sb);
            //doc.Save(sw);

            //var xml = sb.ToString();
            //var noEncoding = xml.Replace(" encoding=\"utf-8\"", "");

            //Regex rRemScript = new Regex(@"<script[^>]*>[\s\S]*?</script>");
            //var noScript = rRemScript.Replace(body.OuterHtml, "");

            //var encoded = EscapeXml(body.OuterHtml);

            var insertedId = _db.ExecuteProcedureScalar("stg.InsertExtractItem", new Dictionary<string, object>() {
                { "extractId", _extractId },
                { "itemName", name },
                { "itemPath", filePath },
                { "content", html },
                { "url", url },
                { "pageType", pageType },
                { "originUrl", originUrl }
            });

            insertedItemId = int.Parse(insertedId.ToString());

                /*
                 	@extractId INT,
	@itemName NVARCHAR(MAX),
	@content NVARCHAR(MAX),
	@itemPath NVARCHAR(MAX)
                 */
            return doc;
        }

        private static string EscapeXml(string s)
        {
            string toxml = s;
            if (!string.IsNullOrEmpty(toxml))
            {
                // replace literal values with entities
                toxml = toxml.Replace("&", "&amp;");
                toxml = toxml.Replace("'", "&apos;");
                //toxml = toxml.Replace("'", "&apos;");

                //toxml = toxml.Replace("\"", "&quot;");
                //toxml = toxml.Replace(">", "&gt;");
                //toxml = toxml.Replace("<", "&lt;");
            }
            return toxml;
        }


        private static void WriteToCsv(DataTable dt, string path)
        {
            StringBuilder sb = new StringBuilder();

            IEnumerable<string> columnNames = dt.Columns.Cast<DataColumn>().
                                              Select(column => column.ColumnName);
            sb.AppendLine(string.Join(",", columnNames));

            foreach (DataRow row in dt.Rows)
            {
                IEnumerable<string> fields = row.ItemArray.Select(field => field.ToString());
                sb.AppendLine(string.Join(",", fields));
            }

            File.WriteAllText(path, sb.ToString());
        }





    }
}
