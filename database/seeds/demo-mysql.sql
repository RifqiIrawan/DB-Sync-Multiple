-- Database demo MySQL (Endpoint A) untuk uji DBSync
USE hrga;
CREATE TABLE departments (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL);
CREATE TABLE employees (
  id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, dept_id INT NULL,
  salary DECIMAL(10,2) NOT NULL, active TINYINT(1) NOT NULL DEFAULT 1, hired DATE NULL,
  updated_at DATETIME NULL, notes TEXT NULL, FOREIGN KEY (dept_id) REFERENCES departments(id));
INSERT INTO departments VALUES (1,'HR'),(2,'IT'),(3,'Finance');
INSERT INTO employees (name,dept_id,salary,active,hired,updated_at,notes) VALUES
 ('Andi',1,5000000.50,1,'2020-01-15','2026-01-01 10:00:00','Unicode: éñ 日本'),
 ('Budi',2,7500000.00,1,'2021-06-01','2026-01-02 11:00:00',NULL),
 ('Citra',2,6200000.25,0,'2019-03-20','2026-01-03 12:00:00','x');
-- user khusus DBSync (bukan root); hak CDC (binlog) ikut diberikan
CREATE USER 'dbsync'@'%' IDENTIFIED BY 'dbsync_demo_pw';
GRANT SELECT, INSERT, UPDATE, DELETE ON hrga.* TO 'dbsync'@'%';
GRANT SELECT, RELOAD, SHOW DATABASES, REPLICATION SLAVE, REPLICATION CLIENT ON *.* TO 'dbsync'@'%';
FLUSH PRIVILEGES;