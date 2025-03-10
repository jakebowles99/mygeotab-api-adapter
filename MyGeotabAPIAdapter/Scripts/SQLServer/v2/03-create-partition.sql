USE [geotabadapterdb];
EXEC [dbo].[spManagePartitions] @MinDateTimeUTC = '2025-01-01', @PartitionInterval = 'monthly';