using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Sitecore.Abstractions;
using Sitecore.Buckets.Extensions;
using Sitecore.Caching;
using Sitecore.ContentSearch.Utilities;
using Sitecore.Data;
using Sitecore.Data.Events;
using Sitecore.Data.Items;
using Sitecore.DependencyInjection;
using Sitecore.Diagnostics;
using Sitecore.Events;
using Sitecore.Links;
using Sitecore.Publishing;
using Sitecore.Sites;
using Sitecore.Web;
using Sitecore.XA.Foundation.Abstractions.Configuration;
using Sitecore.XA.Foundation.Multisite;
using Sitecore.XA.Foundation.Multisite.Extensions;
using Sitecore.XA.Foundation.SitecoreExtensions.Repositories;


namespace Sitecore.Support.XA.Foundation.Multisite.EventHandlers
{
    public class HtmlCacheClearer
    {
        private readonly IEnumerable<ID> _fieldIds;
        protected ISiteInfoResolver SiteInfoResolver;
        protected ISharedSitesContext SharedSitesContext;
        protected IMultisiteContext MultisiteContext;
        protected IDatabaseRepository DatabaseRepository;
        protected BaseSiteManager SiteManager;
        protected BaseCacheManager CacheManager;
        protected BaseFactory Factory;

        public HtmlCacheClearer()
        {
            SiteInfoResolver = ServiceLocator.ServiceProvider.GetService<ISiteInfoResolver>();
            SharedSitesContext = ServiceLocator.ServiceProvider.GetService<ISharedSitesContext>();
            MultisiteContext = ServiceLocator.ServiceProvider.GetService<IMultisiteContext>();
            DatabaseRepository = ServiceLocator.ServiceProvider.GetService<IDatabaseRepository>();
            SiteManager = ServiceLocator.ServiceProvider.GetService<BaseSiteManager>();
            Factory = ServiceLocator.ServiceProvider.GetService<BaseFactory>();
            CacheManager = ServiceLocator.ServiceProvider.GetService<BaseCacheManager>();
            _fieldIds = ServiceLocator.ServiceProvider.GetService<IConfiguration<MultisiteConfiguration>>().GetConfiguration().HtmlCacheClearerFieldIds;
        }

        public new void OnPublishEnd(object sender, EventArgs args)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(args, "args");
            if (args is SitecoreEventArgs sitecoreEventArgs)
            {
                if (sitecoreEventArgs.Parameters[0] is Publisher publisher)
                {
                    if (publisher.Options.RootItem != null)
                    {
                        List<SiteInfo> sitesToClear = GetUsages(publisher.Options.RootItem);
                        if (sitesToClear.Count > 0)
                        {
                            sitesToClear.ForEach(ClearSiteCache);
                            return;
                        }
                    }
                }
            }

            ClearAllSxaSitesCaches();
        }

        public new void OnPublishEndRemote(object sender, EventArgs args)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(args, "args");
            if (args is PublishEndRemoteEventArgs sitecoreEventArgs)
            {
                Database database = Factory.GetDatabase(sitecoreEventArgs.TargetDatabaseName, false);
                Item rootItem = database?.GetItem(new ID(sitecoreEventArgs.RootItemId));
                if (rootItem != null)
                {
                    List<SiteInfo> sitesToClear = GetUsages(rootItem);
                    if (sitesToClear.Count > 0)
                    {
                        sitesToClear.ForEach(ClearSiteCache);
                        return;
                    }
                }
            }

            ClearAllSxaSitesCaches();
        }

        protected virtual void ClearAllSxaSitesCaches()
        {
            SiteManager.GetSites().Where(site => site.IsSxaSite()).Select(site => site.Name).ForEach(ClearSiteCache);
        }

        private void ClearSiteCache(string siteName)
        {
            Log.Info($"HtmlCacheClearer clearing cache for {siteName} site", this);
            ProcessSite(siteName);
            Log.Info("HtmlCacheClearer done.", this);
        }


        private void ClearSiteCache(SiteInfo site)
        {
            ClearSiteCache(site.Name);
        }

        private void ProcessSite(string siteName)
        {
            SiteContext site = Factory.GetSite(siteName);
            if (site != null)
            {
                HtmlCache htmlCache = CacheManager.GetHtmlCache(site);
                htmlCache?.Clear();
            }
        }

        private List<SiteInfo> GetUsages(Item item)
        {
            Assert.IsNotNull(item, "item");

            List<SiteInfo> usages = new List<SiteInfo>();
            var currentItem = item;
            do
            {
                if (currentItem.IsSxaSite())
                {
                    SiteInfo usage = SiteInfoResolver.GetSiteInfo(currentItem);
                    if (usage != null)
                    {
                        usages.Add(usage);
                        break;
                    }
                }

                ItemLink[] itemReferrers = Globals.LinkDatabase.GetItemReferrers(currentItem, false);
                foreach (ItemLink link in itemReferrers)
                {
                    if (IsOneOfWanted(link.SourceFieldID))
                    {
                        Item sourceItem = link.GetSourceItem();
                        SiteInfo sourceItemSite = SiteInfoResolver.GetSiteInfo(sourceItem);
                        usages.Add(sourceItemSite);
                    }
                }
                currentItem = currentItem.Parent;
            } while (currentItem != null);

            usages = usages.Where(s => s != null).GroupBy(g => new { g.Name }).Select(x => x.First()).ToList();
            usages.AddRange(GetAllSitesForSharedSites(usages));
            return usages;
        }

        protected virtual IEnumerable<SiteInfo> GetAllSitesForSharedSites(IEnumerable<SiteInfo> usages)
        {
            var sInfo = usages.FirstOrDefault(info => !info.Database.IsNullOrEmpty() && info.Database != "core");
            if (sInfo == null)
            {
                return new SiteInfo[0];
            }
            var database = DatabaseRepository.GetDatabase(sInfo.Database);
            List<SiteInfo> additionaSites = new List<SiteInfo>();
            foreach (var siteInfo in usages)
            {
                var siteRoot = database.GetItem(SiteInfoResolver.GetRootPath(siteInfo));
                if (IsSharedSite(siteRoot))
                {
                    var tenantItem = MultisiteContext.GetTenantItem(siteRoot);

                    var siteInfos = SiteInfoResolver.Sites
                        .Where(info => info.RootPath.Contains(tenantItem.Paths.Path))
                        .Where(info => usages.All(x => x.Name != info.Name))
                        .Where(info => additionaSites.All(x => x.Name != info.Name))
                        .ToList();

                    additionaSites.AddRange(siteInfos);
                }
            }
            return additionaSites;
        }

        protected virtual bool IsSharedSite(Item item)
        {
            var siteItem = MultisiteContext.GetSiteItem(item);
            return SharedSitesContext.GetSharedSites(siteItem).Any(sharedSite => sharedSite.ID == siteItem.ID);
        }

        private IEnumerable<ID> GetFieldIdsFromConfiguration()
        {
            var xmlNodes = Factory.GetConfigNodes("experienceAccelerator/multisite/htmlCacheClearer/fieldID").Cast<XmlNode>();
            return xmlNodes.Select(node => new ID(node.InnerText)).ToList();
        }

        private bool IsOneOfWanted(ID sourceFieldId)
        {
            return _fieldIds.Any(x => x.Equals(sourceFieldId));
        }
    }
}