﻿using System;
using System.Configuration;
using System.Data.SqlClient;
using GIS.SQL2008;
using GIS.ArcGIS;
using GIS.Framework;
using GIS.Framework.Ao.Layers;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.esriSystem;
using System.IO;
using Castle.Windsor;
using Castle.Windsor.Configuration.Interpreters;
using log4net;
using log4net.Config;
using System.Collections.Generic;

namespace SQL2008Import
{
    class Program
    {
        private static readonly log4net.ILog _log = LogManager.GetLogger( typeof(Program) );

        static void Main( string[] args )
        {
            string[] prefixes = new string[] { "/f$", "/t$", "/o$" };
            CmdArgExtractor cae = new CmdArgExtractor( prefixes, '/', '$' );

            WindsorContainer container = new WindsorContainer( new XmlInterpreter() );
            XmlConfigurator.Configure();

            IAoInitialize license = new AoInitializeClass();
            license.Initialize( esriLicenseProductCode.esriLicenseProductCodeArcEditor );

            ConnectionStringSettings connStr = ConfigurationManager.ConnectionStrings[ "SQL2008" ];
            SqlConnection sqlConnection = new SqlConnection( connStr.ConnectionString );
            sqlConnection.Open();
            SQL2008Database sqlDatabase = new SQL2008Database( connStr.ConnectionString );

            if( ( args.Length != 0 ) && ( args[ 0 ].Contains( "?" ) ) )
            {
                Console.WriteLine( "Please use the /f$ option to specify the shapefile path" );
                Console.WriteLine( "(OPTIONAL) the /t$ option to specify a table name. If not specified, the name of the shapefile will be used as the table name." );
                Console.WriteLine( "(OPTIONAL) the /o$ option to specify whether to overwrite (Y/N) if a table with the same name exists. If not specified, the existing table will not be overwritten and the import will be aborted." );
                return;
            }

            //if( !cae.ValidArgsPrefixes( args ) )
            //{
            //    Console.WriteLine( "Invalid parameters specified." );
            //    Console.WriteLine( "" );
            //    Console.WriteLine( "Please use the /f: option to specify the shapefile path" );
            //    Console.WriteLine( "(OPTIONAL) the /t: option to specify a table name. If not specified, the name of the shapefile will be used as the table name." );
            //    Console.WriteLine( "(OPTIONAL) the /o: option to specify whether to overwrite if a table with the same name exists. If not specified, the existing table will not be overwritten and the import will be aborted." );
            //    return;
            //}

            string[ , ] inputParameters = cae.GetArgsTwoDimArray( args );

            Dictionary<string, string> inputParams = new Dictionary<string, string>();
            for( int i = 0; i < args.Length; i++ )
            {
                inputParams.Add( inputParameters[ 0, i ].ToLower(), inputParameters[ 1, i ].ToLower() );
            }

            if( !inputParams.ContainsKey( "f" ) )
            {
                Console.WriteLine( "A shapefile path must be specified to continue." );
                return;
            }

            string inputShapeFile = inputParams["f"];

            if( !File.Exists( inputShapeFile ) )
            {
                Console.WriteLine( "The shapefile path is not valid." );
                return;
            }

            string shapefileDirectory = "C:\\SHPData\\WGS84";
            string shapefile = "natarea";

            shapefileDirectory = Path.GetDirectoryName(Path.GetFullPath(inputShapeFile));
            shapefile = Path.GetFileNameWithoutExtension(inputShapeFile);
            string tableName = shapefile;

            if( inputParams.ContainsKey( "t" ) )
            {
                tableName = inputParams[ "t" ];
            }

            if( sqlDatabase.ContainsTable( tableName ) )
            {
                Console.WriteLine( "A table with the same name '" + tableName + "' already exists in the database." );
                if( inputParams.ContainsKey( "o" ) && ((inputParams["o"] == "y") || (inputParams["o"] == "yes")))
                {
                    Console.WriteLine( "Deleting the existing table and creating a new table." );
                    sqlDatabase.DeleteTable( tableName );
                }
                else
                {
                    Console.WriteLine( "Aborting import." );
                    return;
                }
            }

            IWorkspace workspace = GeodatabaseUtil.GetShapefileWorkspace( shapefileDirectory );
            IFeatureWorkspace featureWorkspace = workspace as IFeatureWorkspace;
            IFeatureClass featureClass = featureWorkspace.OpenFeatureClass( shapefile );

            DefaultAoGeometryTypeProvider aoGeometryTypeProvider = new DefaultAoGeometryTypeProvider();
            string geometryType = aoGeometryTypeProvider.GetGeometryType( featureClass.ShapeType );

            if( String.IsNullOrEmpty( geometryType ) )
            {
                Console.WriteLine( "The geometry type of the source FeatureClass is not supported." );
                Console.ReadLine();
                return;
            }

            System.Collections.IDictionary parameters = new System.Collections.Hashtable();
            parameters.Add( "featureClass", featureClass );
            parameters.Add( "layerName", tableName );
            parameters.Add( "keyFieldName", featureClass.OIDFieldName );
            AoFCLayer shapeFileLayer = container.Resolve<AoFCLayer>("Ao" + geometryType, parameters);

            if( shapeFileLayer == null )
            {
                Console.WriteLine( "Unable to create the input framework layer for the FeatureClass. Please check the component configuration." );
                Console.ReadLine();
                return;
            }

            ISupportsGISFields shapeFileFields = shapeFileLayer as ISupportsGISFields;
            IGISFields inputFields = shapeFileFields.GetGISFields();

            SqlCommand sqlCommand;

            int? srid = null;
            if( shapeFileLayer is ISupportsSRID )
                srid = ( shapeFileLayer as ISupportsSRID ).Srid;

            IGISLayer sql2008Layer = null;
            //Create the dictionary with the parameters to create the corresponding SQL 2008 layer
            System.Collections.IDictionary sqlParameters = new System.Collections.Hashtable();
            sqlParameters.Add("tableName", tableName);
            sqlParameters.Add("shapeFieldName", "SHAPE");
            sqlParameters.Add("layerName", tableName);
            sqlParameters.Add("keyFieldName", featureClass.OIDFieldName);

            if( srid.HasValue && WKIDRanges.IsGeographic(srid.Value))
            {
                //Create the geography table with the fields from the FeatureClass
                sqlDatabase.CreateGeographyTable( inputFields, tableName, "SHAPE" );
                sqlCommand = new SqlCommand( "Select * from " + tableName, sqlConnection );
                sqlParameters.Add("dbCommand", sqlCommand);
                sql2008Layer = container.Resolve<IGISLayer>("Sql2008Geog" + geometryType, sqlParameters);
            }
            else
            {
                //Create the geometry table with the fields from the FeatureClass
                sqlDatabase.CreateGeometryTable( inputFields, tableName, "SHAPE" );
                sqlCommand = new SqlCommand( "Select * from " + tableName, sqlConnection );
                sqlParameters.Add("dbCommand", sqlCommand);
                sql2008Layer = container.Resolve<IGISLayer>("Sql2008Geom" + geometryType, sqlParameters);
            }

            //Get the editable layer reference to the destination sql table layer
            IGISEditableLayer editableLayer = sql2008Layer as IGISEditableLayer;

            //Quit the application if a valid editable layer reference could not be obtained
            if( editableLayer == null )
            {
                Console.WriteLine( "The destination is either invalid or does not support editing." );
                return;
            }

            Console.WriteLine( "Importing features..." );
            int count = 0;

            shapeFileLayer.Search( null );
            while( shapeFileLayer.MoveNext() )
            {
                try
                {
                    editableLayer.Add( shapeFileLayer.Current );
                }
                catch( Exception ex )
                {
                    _log.Error( "An unexpected error occured while adding feature. " + ex.Message, ex );
                }
                Console.SetCursorPosition( 0, Console.CursorTop );
                Console.Write( ++count );
            }

            license.Shutdown();

            Console.WriteLine();
            Console.WriteLine( "Import completed. Press any key to exit." );
            Console.ReadKey( false );
        }
    }
}
