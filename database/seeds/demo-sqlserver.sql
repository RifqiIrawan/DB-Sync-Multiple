-- Jalankan setelah container demo-sqlserver siap:
--   sqlcmd -S localhost,1434 -U sa -P "Demo_Passw0rd!" -C -i database/seeds/demo-sqlserver.sql
IF DB_ID('hrga_mssql') IS NULL CREATE DATABASE hrga_mssql;
GO
USE hrga_mssql;
GO
CREATE TABLE dbo.departments (id INT PRIMARY KEY, name NVARCHAR(100) NOT NULL);
CREATE TABLE dbo.employees (id INT NOT NULL PRIMARY KEY, name NVARCHAR(100) NOT NULL, dept_id INT NULL, salary DECIMAL(10,2) NOT NULL, active BIT NOT NULL DEFAULT 1, hired DATE NULL, updated_at DATETIME2 NULL, notes NVARCHAR(MAX) NULL);
GO
CREATE LOGIN dbsync WITH PASSWORD = 'Dbsync_Demo_Pw1!', CHECK_POLICY = OFF;
CREATE USER dbsync FOR LOGIN dbsync;
ALTER ROLE db_datareader ADD MEMBER dbsync; ALTER ROLE db_datawriter ADD MEMBER dbsync;
GRANT ALTER ON SCHEMA::dbo TO dbsync;   -- diperlukan untuk SET IDENTITY_INSERT (hanya bila tabel punya identity)
GO
-- CDC (opsional, mode CDC): EXEC sys.sp_cdc_enable_db; EXEC sys.sp_cdc_enable_table @source_schema='dbo', @source_name='employees', @role_name=NULL;