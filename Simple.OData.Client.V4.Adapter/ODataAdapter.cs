﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Xml;
using Microsoft.OData.Core;
using Microsoft.OData.Core.UriParser;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.Spatial;

#pragma warning disable 1591

namespace Simple.OData.Client
{
    public static class V4Adapter
    {
        public static void Reference() { }
    }
}

namespace Simple.OData.Client.V4.Adapter
{
    public class ODataAdapter : ODataAdapterBase
    {
        private readonly ISession _session;

        public override AdapterVersion AdapterVersion { get { return AdapterVersion.V4; } }

        public override ODataPayloadFormat DefaultPayloadFormat
        {
            get { return ODataPayloadFormat.Json; }
        }

        public new IEdmModel Model
        {
            get { return base.Model as IEdmModel; }
            set { base.Model = value; }
        }

        private ODataAdapter(ISession session, string protocolVersion)
        {
            _session = session;
            ProtocolVersion = protocolVersion;

            CustomConverters.RegisterTypeConverter(typeof(GeographyPoint), TypeConverters.CreateGeographyPoint);
            CustomConverters.RegisterTypeConverter(typeof(GeometryPoint), TypeConverters.CreateGeometryPoint);
        }

        public ODataAdapter(ISession session, string protocolVersion, HttpResponseMessage response)
            : this(session, protocolVersion)
        {
            var readerSettings = new ODataMessageReaderSettings
            {
                MessageQuotas = { MaxReceivedMessageSize = Int32.MaxValue }
            };
            using (var messageReader = new ODataMessageReader(new ODataResponseMessage(response), readerSettings))
            {
                Model = messageReader.ReadMetadataDocument();
            }
        }

        public ODataAdapter(ISession session, string protocolVersion, string metadataString)
            : this(session, protocolVersion)
        {
            var reader = XmlReader.Create(new StringReader(metadataString));
            reader.MoveToContent();
            Model = EdmxReader.Parse(reader);
        }

        public override string GetODataVersionString()
        {
            switch (this.ProtocolVersion)
            {
                case ODataProtocolVersion.V4:
                    return "V4";
            }
            throw new InvalidOperationException(string.Format("Unsupported OData protocol version: \"{0}\"", this.ProtocolVersion));
        }

        public override string ConvertValueToUriLiteral(object value)
        {
            return value is ODataExpression
                ? (value as ODataExpression).AsString(_session)
                : ODataUriUtils.ConvertToUriLiteral(value,
                    (ODataVersion)Enum.Parse(typeof(ODataVersion), this.GetODataVersionString(), false), this.Model);
        }

        public override FunctionFormat FunctionFormat
        {
            get { return FunctionFormat.Key; }
        }

        public override IMetadata GetMetadata()
        {
            return new Metadata(_session, Model);
        }

        public override IResponseReader GetResponseReader()
        {
            return new ResponseReader(_session, Model);
        }

        public override IRequestWriter GetRequestWriter(Lazy<IBatchWriter> deferredBatchWriter)
        {
            return new RequestWriter(_session, Model, deferredBatchWriter);
        }

        public override IBatchWriter GetBatchWriter()
        {
            return new BatchWriter(_session);
        }

        public override void FormatCommandClauses(
            IList<string> commandClauses,
            EntityCollection entityCollection,
            IList<KeyValuePair<string, ODataExpandOptions>> expandAssociations,
            IList<string> selectColumns,
            IList<KeyValuePair<string, bool>> orderbyColumns,
            bool includeCount)
        {
            if (expandAssociations.Any())
            {
                commandClauses.Add(string.Format("{0}={1}", ODataLiteral.Expand,
                    string.Join(",", expandAssociations.Select(x =>
                        FormatExpansionSegment(x.Key, entityCollection,
                        x.Value,
                        SelectExpansionSegmentColumns(selectColumns, x.Key),
                        SelectExpansionSegmentColumns(orderbyColumns, x.Key))))));
            }

            selectColumns = SelectExpansionSegmentColumns(selectColumns, null);
            FormatClause(commandClauses, entityCollection, selectColumns, ODataLiteral.Select, FormatSelectItem);

            orderbyColumns = SelectExpansionSegmentColumns(orderbyColumns, null);
            FormatClause(commandClauses, entityCollection, orderbyColumns, ODataLiteral.OrderBy, FormatOrderByItem);

            if (includeCount)
            {
                commandClauses.Add(string.Format("{0}={1}", ODataLiteral.Count, ODataLiteral.True));
            }
        }

        private string FormatExpansionSegment(string path, EntityCollection entityCollection, 
            ODataExpandOptions expandOptions, IList<string> selectColumns, IList<KeyValuePair<string, bool>> orderbyColumns)
        {
            var items = path.Split('/');
            var associationName = _session.Metadata.GetNavigationPropertyExactName(entityCollection.Name, items.First());

            var clauses = new List<string>();
            var text = associationName;
            if (expandOptions.ExpandMode == ODataExpandMode.ByReference)
                text += "/" + ODataLiteral.Ref;

            if (items.Count() > 1)
            {
                path = path.Substring(items.First().Length + 1);
                entityCollection = _session.Metadata.GetEntityCollection(
                    _session.Metadata.GetNavigationPropertyPartnerName(entityCollection.Name, associationName));

                clauses.Add(string.Format("{0}={1}", ODataLiteral.Expand,
                    FormatExpansionSegment(path, entityCollection, expandOptions,
                    SelectExpansionSegmentColumns(selectColumns, associationName),
                    SelectExpansionSegmentColumns(orderbyColumns, associationName))));
            }

            if (expandOptions.Levels > 1)
            {
                clauses.Add(string.Format("{0}={1}", ODataLiteral.Levels, expandOptions.Levels));
            }
            else if (expandOptions.Levels == 0)
            {
                clauses.Add(string.Format("{0}={1}", ODataLiteral.Levels, ODataLiteral.Max));
            }

            if (selectColumns.Any())
            {
                var columns = string.Join(",", SelectExpansionSegmentColumns(selectColumns, null));
                if (!string.IsNullOrEmpty(columns))
                    clauses.Add(string.Format("{0}={1}", ODataLiteral.Select, columns));
            }

            if (orderbyColumns.Any())
            {
                var columns = string.Join(",", SelectExpansionSegmentColumns(orderbyColumns, null)
                    .Select(x => x.Key + (x.Value ? " desc" : string.Empty)).ToList());
                if (!string.IsNullOrEmpty(columns))
                    clauses.Add(string.Format("{0}={1}", ODataLiteral.OrderBy, columns));
            }

            if (clauses.Any())
                text += string.Format("({0})", string.Join(";", clauses));

            return text;
        }

        private IList<string> SelectExpansionSegmentColumns(
            IList<string> columns, string path)
        {
            if (string.IsNullOrEmpty(path))
                return columns.Where(x => !x.Contains("/")).ToList();
            else
                return columns.Where(x => x.Contains("/") && x.Split('/').First() == path.Split('/').First())
                    .Select(x => string.Join("/", x.Split('/').Skip(1))).ToList();
        }

        private IList<KeyValuePair<string, bool>> SelectExpansionSegmentColumns(
            IList<KeyValuePair<string, bool>> columns, string path)
        {
            if (string.IsNullOrEmpty(path))
                return columns.Where(x => !x.Key.Contains("/")).ToList();
            else
                return columns.Where(x => x.Key.Contains("/") && x.Key.Split('/').First() == path.Split('/').First())
                    .Select(x => new KeyValuePair<string, bool>(
                        string.Join("/", x.Key.Split('/').Skip(1)), x.Value)).ToList();
        }
    }
}