using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Sitecore.Buckets.Extensions;
using Sitecore.Caching;
using Sitecore.Configuration;
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
using Sitecore.XA.Foundation.Multisite.Extensions;


namespace Sitecore.XA.Foundation.Multisite.EventHandlers
{
    public class HtmlCacheClearer : Publishing.HtmlCacheClearer
    {
        private readonly IEnumerable<ID> _fieldIds;
        protected ISiteInfoResolver SiteInfoResolver;
        protected ISharedSitesContext SharedSitesContext;
        protected IMultisiteContext MultisiteContext;

        public HtmlCacheClearer()
        {
            var xmlNodes = Factory.GetConfigNodes("experienceAccelerator/multisite/htmlCacheClearer/fieldID").Cast<XmlNode>();
            SiteInfoResolver = ServiceLocator.ServiceProvider.GetService<ISiteInfoResolver>();
            SharedSitesContext = ServiceLocator.ServiceProvider.GetService<ISharedSitesContext>(); ;
            MultisiteContext = ServiceLocator.ServiceProvider.GetService<IMultisiteContext>(); ;
            _fieldIds = xmlNodes.Select(node => new ID(node.InnerText));
        }

        public void OnPublishEnd(object sender, EventArgs args)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(args, "args");
            var sitecoreEventArgs = args as SitecoreEventArgs;
            if (sitecoreEventArgs != null)
            {
                var publisher = sitecoreEventArgs.Parameters[0] as Publisher;
                if (publisher != null)
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
            base.ClearCache(sender, args);
            ClearAllSxaSitesCaches();
        }

        public void OnPublishEndRemote(object sender, EventArgs args)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(args, "args");
            var sitecoreEventArgs = args as PublishEndRemoteEventArgs;
            if (sitecoreEventArgs != null)
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
            base.ClearCache(sender, args);
            ClearAllSxaSitesCaches();
        }

        protected virtual void ClearAllSxaSitesCaches()
        {
            SiteManager.GetSites().Where(site => site.IsSxaSite()).Select(site => site.Name).ForEach(ClearSiteCache);
        }

        private void ClearSiteCache(string siteName)
        {
            Log.Info(String.Format("HtmlCacheClearer clearing cache for {0} site", siteName), this);
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
                if (htmlCache != null)
                {
                    htmlCache.Clear();
                }
            }
        }

        private List<SiteInfo> GetUsages(Item item)
        {
            Assert.IsNotNull(item, "item");

            List<SiteInfo> usages = new List<SiteInfo>();
            var currentItem = item;
            do
            {
                var siteItem = MultisiteContext.GetSiteItem(currentItem);
                if (siteItem != null)
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
            var database = Database.GetDatabase(sInfo.Database);
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

        private bool IsOneOfWanted(ID sourceFieldId)
        {
            return _fieldIds.Any(x => x.Equals(sourceFieldId));
        }
    }
}