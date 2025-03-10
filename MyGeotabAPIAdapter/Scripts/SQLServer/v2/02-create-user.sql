USE [master];
CREATE LOGIN [geotabadapter_client] WITH 
	PASSWORD = N'$(DB_PASSWORD)',
	DEFAULT_DATABASE=[geotabadapterdb], 
	DEFAULT_LANGUAGE=[us_english], 
	CHECK_EXPIRATION=OFF, 
	CHECK_POLICY=OFF;

USE [geotabadapterdb];
CREATE USER [geotabadapter_client] FOR LOGIN [geotabadapter_client] WITH DEFAULT_SCHEMA=[dbo];
ALTER ROLE [db_owner] ADD MEMBER [geotabadapter_client];