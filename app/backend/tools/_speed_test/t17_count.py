import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
print(c.execute("select count(*) from download_tasks where status='failed'").fetchone()[0])
