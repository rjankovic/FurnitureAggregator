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
            var articleDescription = artikelHtml.DocumentNode.SelectSingleNode("//div[@class='article__description']");
            ExtractScontoArtikelDescription(articleDescription, attributesTable);

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

            var deliveryAddToCart = articleInformationInner.ChildNodes.First(x => x.HasClass("articleInformation__deliveryAddToCart"));
            var availability = deliveryAddToCart.Descendants().First(x => x.HasClass("articleDeliveryTimeText__availability"));
            var availabilityLastChild = availability.LastChild;
            var availabilityText = availabilityLastChild.InnerText;
            AddAttributesTableRow(attributesTable, "Availability", "Availability", availabilityText);

            //<div class="articleInformation__deliveryAddToCart" data-articleno="413626501"><div class="deliveryAddToCart deliveryAddToCart--infoBlock"><div class="deliveryAddToCart__addToCart deliveryAddToCart__addToCart--infoBlock deliveryAddToCart__addToCart--noInput" id="articlePresentationAddToCart"><div class="addArticlesToCart"><div class="addArticlesToCart__addToCartButton addArticlesToCart__addToCartButton--infoBlock"><button class="button button--addToCart" id="add-to-cart-logistic" name="add-to-cart-logistic" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__icon button__icon--addToCart"></div><div class="button__label">Přidat do košíku</div></div></button><button class="button button--grey button--hidden" id="fakeButton-stock" name="fakeButton-stock" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__label button__label--grey">Vyprodáno</div><div class="button__icon button__icon--grey"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button><button class="button button--grey button--hidden" id="fakeButton-plz" name="fakeButton-plz" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__label button__label--grey">Zadejte vaše PSČ</div><div class="button__icon button__icon--grey"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button><button class="button button--grey button--hidden" id="fakeButton-store" name="fakeButton-store" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__label button__label--grey">ads.productAvailability.errorText.availableAtStore</div><div class="button__icon button__icon--grey"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button></div></div></div><div class="deliveryAddToCart__amount deliveryAddToCart__amount--infoBlock"><div class="addArticlesToCart__quantityField addArticlesToCart__quantityField--infoBlock" data-articleno="413626501"><div class="selectBox selectBox--infoBlock" id="quantityOfArticles"><label class="selectBox__label selectBox__label--infoBlock"> </label><select class="selectBox__select selectBox__select--infoBlock"><option value="1" selected="selected" data-to-email="">1</option><option value="2" data-to-email="">2</option><option value="3" data-to-email="">3</option><option value="4" data-to-email="">4</option><option value="5" data-to-email="">5</option><option value="6" data-to-email="">6</option><option value="7" data-to-email="">7</option><option value="8" data-to-email="">8</option><option value="9" data-to-email="">9</option><option value="10" data-to-email="">10</option></select></div></div></div><div class="deliveryAddToCart__deliveryTime deliveryAddToCart__deliveryTime--infoBlock" id="articlePresentationDeliveryTime"><div class="articleDeliveryTimeText"><span class="articleDeliveryTimeText__overwritingText articleDeliveryTimeText__overwritingText--hidden"></span><ul class="articleDeliveryTimeText__availability  articleDeliveryTimeText--"><li class="bulletList__bulletPoint--ADS articleDeliveryTimeText__deliveryModificationText articleDeliveryTimeText__deliveryModificationText--infoBlock">Dostupnost: </li><li class="bulletList__bulletPoint--ADS articleDeliveryTimeText__deliveryTimeText articleDeliveryTimeText__deliveryTimeText--infoBlock articleDeliveryTimeText__deliveryTimeText--lowerThan5Days">Skladem</li></ul></div></div></div></div>
            //<div class="deliveryAddToCart deliveryAddToCart--infoBlock"><div class="deliveryAddToCart__addToCart deliveryAddToCart__addToCart--infoBlock deliveryAddToCart__addToCart--noInput" id="articlePresentationAddToCart"><div class="addArticlesToCart"><div class="addArticlesToCart__addToCartButton addArticlesToCart__addToCartButton--infoBlock"><button class="button button--addToCart" id="add-to-cart-logistic" name="add-to-cart-logistic" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__icon button__icon--addToCart"></div><div class="button__label">Přidat do košíku</div></div></button><button class="button button--grey button--hidden" id="fakeButton-stock" name="fakeButton-stock" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__label button__label--grey">Vyprodáno</div><div class="button__icon button__icon--grey"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button><button class="button button--grey button--hidden" id="fakeButton-plz" name="fakeButton-plz" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__label button__label--grey">Zadejte vaše PSČ</div><div class="button__icon button__icon--grey"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button><button class="button button--grey button--hidden" id="fakeButton-store" name="fakeButton-store" type="button" data-articleno="413626501" data-w2kid="413626501"><div class="button__wrapper"><div class="button__label button__label--grey">ads.productAvailability.errorText.availableAtStore</div><div class="button__icon button__icon--grey"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button></div></div></div><div class="deliveryAddToCart__amount deliveryAddToCart__amount--infoBlock"><div class="addArticlesToCart__quantityField addArticlesToCart__quantityField--infoBlock" data-articleno="413626501"><div class="selectBox selectBox--infoBlock" id="quantityOfArticles"><label class="selectBox__label selectBox__label--infoBlock"> </label><select class="selectBox__select selectBox__select--infoBlock"><option value="1" selected="selected" data-to-email="">1</option><option value="2" data-to-email="">2</option><option value="3" data-to-email="">3</option><option value="4" data-to-email="">4</option><option value="5" data-to-email="">5</option><option value="6" data-to-email="">6</option><option value="7" data-to-email="">7</option><option value="8" data-to-email="">8</option><option value="9" data-to-email="">9</option><option value="10" data-to-email="">10</option></select></div></div></div><div class="deliveryAddToCart__deliveryTime deliveryAddToCart__deliveryTime--infoBlock" id="articlePresentationDeliveryTime"><div class="articleDeliveryTimeText"><span class="articleDeliveryTimeText__overwritingText articleDeliveryTimeText__overwritingText--hidden"></span><ul class="articleDeliveryTimeText__availability  articleDeliveryTimeText--"><li class="bulletList__bulletPoint--ADS articleDeliveryTimeText__deliveryModificationText articleDeliveryTimeText__deliveryModificationText--infoBlock">Dostupnost: </li><li class="bulletList__bulletPoint--ADS articleDeliveryTimeText__deliveryTimeText articleDeliveryTimeText__deliveryTimeText--infoBlock articleDeliveryTimeText__deliveryTimeText--lowerThan5Days">Skladem</li></ul></div></div></div>
            //<ul class="articleDeliveryTimeText__availability  articleDeliveryTimeText--"><li class="bulletList__bulletPoint--ADS articleDeliveryTimeText__deliveryModificationText articleDeliveryTimeText__deliveryModificationText--infoBlock">Dostupnost: </li><li class="bulletList__bulletPoint--ADS articleDeliveryTimeText__deliveryTimeText articleDeliveryTimeText__deliveryTimeText--infoBlock articleDeliveryTimeText__deliveryTimeText--lowerThan5Days">Skladem</li></ul>
        }

        public static void ExtractScontoArtikelDescription(HtmlNode articleDescription, DataTable attributesTable)
        {
            var boxContent = articleDescription.Descendants().First(x => x.HasClass("articleDescription__boxContent"));
            
            var description = boxContent.Descendants().FirstOrDefault(x => x.HasClass("articleDescription__description"));
            if (description != null)
            {
                var descriptionText = description.InnerText;
                AddAttributesTableRow(attributesTable, "Description", "Description", descriptionText);
            }

            //<div class="article__description" data-article-description=""><div class="articleDescription"><div class="articleDescription__select articleDescription__select--undefined"><div class="boldTextAndArrowDown boldTextAndArrowDown--opened"><span class="boldTextAndArrowDown__text">Informace o produktu</span></div></div><div class="articleDescription__box articleDescription__box--opened"><div class="articleDescription__boxContent"><div class="articleDescription__boxShadow" style="display: none;"></div><div class="articleDescription__headline"><h1><div class="articleFullName articleFullName__wrapper"><span class="articleFullName__specification">Komoda / noční stolek </span><span class="articleFullName__name"> ROLAND</span></div></h1></div><div class="articleDescription__description">Malá komoda v tradičním stylu. Masivní borovice (lakovaná) 3 zásuvky. Možno využít jako noční stolek. Dodává se v demontovaném stavu. Kvalitní materiály, čistý přírodní vzhled. Odolné kovové pojezdy zásuvek. Poslouží nejen jako malá komoda, ale splní i úlohu&nbsp;nočního stolku. Masiv chráněný transparentním lakem. Dřevěné úchytky s&nbsp;příjemnou ergonomií.</div><div class="articleDescription__featuresLeft"><div class="articleFeatures"><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Hlavní vlastnosti</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Využití i jako noční stolek</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Masivní lakované dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Samostatné zásuvky</span></li></ul></div></div></div><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Rozměry</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Šířka: 42 cm</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Výška: 57 cm</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Hloubka: 35 cm</span></li></ul></div></div></div></div></div><div class="articleDescription__featuresRight"><div class="articleFeatures"><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Značka</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Počet zásuvek: 3 Kus</span></li></ul></div></div></div><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Materiál &amp; Barva</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Materiál korpusu: Masivní dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Druh dřeva / korpusu: Borovice</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Povrchová úprava korpusu: lakovaný</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Materiál přední části: Masivní dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Druh dřeva / dekoru přední části: Borovice</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Povrchová úprava přední desky: lakovaný</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Materiál úchytů: Dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Barva / dekor: borovice</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Barva úchytů: dřevo</span></li></ul></div></div></div></div></div><div class="articleDescription__articleNumber">Číslo produktu 413626501</div><div class="articleDescription__footer"><div class="articleDescriptionFooter"><div class="articleDescriptionFooter__decoration articleDescriptionFooter__decoration--halfSized">* Dodáváme bez dekorací</div></div></div></div><div class="articleDescription__furtherArticlesButton articleDescription__furtherArticlesButton--hidden"><button class="button button--additionalStyle2" type="button"><div class="button__wrapper"><div class="button__label button__label--additionalStyle2">Více informací</div><div class="button__icon button__icon--additionalStyle2"><svg viewBox="0 0 8 13" xmlns="http://www.w3.org/2000/svg" width="8px" height="13px"><defs><style>.sec-cz-arrowRight-1{fill:white;}</style></defs><path class="sec-cz-arrowRight-1" d="M.822,13a1.416,1.416,0,0,1-.6-.2.784.784,0,0,1,0-1.1l5.483-5.19L.224,1.322a.784.784,0,0,1,0-1.1.782.782,0,0,1,1.1,0L8,6.512,1.321,12.8A.761.761,0,0,1,.822,13Z"></path></svg></div></div></button></div></div></div></div>
            //<div class="articleDescription__boxContent"><div class="articleDescription__boxShadow" style="display: none;"></div><div class="articleDescription__headline"><h1><div class="articleFullName articleFullName__wrapper"><span class="articleFullName__specification">Komoda / noční stolek </span><span class="articleFullName__name"> ROLAND</span></div></h1></div><div class="articleDescription__description">Malá komoda v tradičním stylu. Masivní borovice (lakovaná) 3 zásuvky. Možno využít jako noční stolek. Dodává se v demontovaném stavu. Kvalitní materiály, čistý přírodní vzhled. Odolné kovové pojezdy zásuvek. Poslouží nejen jako malá komoda, ale splní i úlohu&nbsp;nočního stolku. Masiv chráněný transparentním lakem. Dřevěné úchytky s&nbsp;příjemnou ergonomií.</div><div class="articleDescription__featuresLeft"><div class="articleFeatures"><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Hlavní vlastnosti</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Využití i jako noční stolek</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Masivní lakované dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Samostatné zásuvky</span></li></ul></div></div></div><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Rozměry</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Šířka: 42 cm</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Výška: 57 cm</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Hloubka: 35 cm</span></li></ul></div></div></div></div></div><div class="articleDescription__featuresRight"><div class="articleFeatures"><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Značka</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Počet zásuvek: 3 Kus</span></li></ul></div></div></div><div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Materiál &amp; Barva</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Materiál korpusu: Masivní dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Druh dřeva / korpusu: Borovice</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Povrchová úprava korpusu: lakovaný</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Materiál přední části: Masivní dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Druh dřeva / dekoru přední části: Borovice</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Povrchová úprava přední desky: lakovaný</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Materiál úchytů: Dřevo</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Barva / dekor: borovice</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Barva úchytů: dřevo</span></li></ul></div></div></div></div></div><div class="articleDescription__articleNumber">Číslo produktu 413626501</div><div class="articleDescription__footer"><div class="articleDescriptionFooter"><div class="articleDescriptionFooter__decoration articleDescriptionFooter__decoration--halfSized">* Dodáváme bez dekorací</div></div></div></div>

            var featuresElements = boxContent.Descendants().Where(x => x.HasClass("articleFeatures__element"));
            foreach (var featureElement in featuresElements)
            {
                var feature = featureElement.ChildNodes.FirstOrDefault(x => x.HasClass("articleFeature"));
                if (feature == null)
                {
                    continue;
                }
                var featureCategory = feature.ChildNodes.First(x => x.HasClass("headline--ADSTabs"));
                var featureCategoryText = featureCategory.InnerText;
                var list = feature.Descendants().First(x => x.HasClass("bulletList"));
                foreach (var listItem in list.ChildNodes)
                {
                    var itemSpan = listItem.Descendants().First(x => x.Name == "span");
                    var itemText = itemSpan.InnerText;
                    if (itemText.Contains(":"))
                    {
                        var split = itemText.Split(':');
                        if (split.Length != 2)
                        {
                            if (split[0] == "Speciální rozměry" || split[0] == "Další údaje k designu")
                            {
                                split = split.Skip(1).ToArray();
                            }
                            else
                            {
                                throw new Exception();
                            }
                        }
                        var itemName = split[0].Trim();
                        var itemValue = split[1].Trim();
                        AddAttributesTableRow(attributesTable, featureCategoryText, itemName, itemValue);
                    }
                    else
                    {
                        var itemValue = itemText.Trim();
                        AddAttributesTableRow(attributesTable, featureCategoryText, featureCategoryText, itemValue);
                    }
                }
            }

            //<div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Šířka: 42 cm</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Výška: 57 cm</span></li><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Hloubka: 35 cm</span></li></ul></div>
            //<div class="articleFeatures__element"><div class="articleFeature"><div class="headline headline--ADSTabs">Značka</div><div class="articleFeature__items"><ul class="bulletList bulletList--darkGreyAndWide bulletList--ADSTabs"><li class="bulletList__bulletPoint bulletList__bulletPoint--darkGreyAndWide bulletList__bulletPoint--ADSTabs"><span>Počet zásuvek: 3 Kus</span></li></ul></div></div></div>
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
