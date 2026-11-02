-- Database demo PostgreSQL (Endpoint B): struktur sama, tanpa data
CREATE TABLE departments (id integer PRIMARY KEY, name varchar(100) NOT NULL);
CREATE TABLE employees (
  id integer PRIMARY KEY, name varchar(100) NOT NULL, dept_id integer REFERENCES departments(id),
  salary numeric(10,2) NOT NULL, active boolean NOT NULL DEFAULT true, hired date,
  updated_at timestamp, notes text);
CREATE ROLE dbsync LOGIN PASSWORD 'dbsync_demo_pw' REPLICATION;
GRANT ALL ON ALL TABLES IN SCHEMA public TO dbsync;
GRANT ALL ON SCHEMA public TO dbsync;
-- Debezium (publication.autocreate.mode=filtered) membuat publication sendiri: butuh CREATE pada database dan kepemilikan tabel.
GRANT CREATE ON DATABASE hrga_replica TO dbsync;
ALTER TABLE departments OWNER TO dbsync;
ALTER TABLE employees OWNER TO dbsync;