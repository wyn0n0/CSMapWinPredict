"""Lock-source integrity tests; no network or model dependencies."""
import copy
import sys
from pathlib import Path
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parent))
from prepare_situation_dependencies import report_entries

class LockTests(unittest.TestCase):
    def entry(self):
        return {"metadata":{"name":"Demo_Package","version":"1.2.3"},
            "download_info":{"url":"https://files.pythonhosted.org/demo-1.2.3-py3-none-any.whl",
            "archive_info":{"hashes":{"sha256":"a"*64}}}}

    def test_normalized_name_and_exact_wheel(self):
        rows=report_entries({"install":[self.entry()]})
        self.assertEqual(rows[0]["name"],"demo-package")
        self.assertEqual(rows[0]["version"],"1.2.3")

    def test_rejects_nonofficial_nonwheel_or_missing_hash(self):
        for url in ("http://files.pythonhosted.org/a.whl","https://example.com/a.whl",
                    "https://files.pythonhosted.org/a.tar.gz","https://files.pythonhosted.org/a.whl?token=secret"):
            row=self.entry(); row["download_info"]["url"]=url
            with self.assertRaises(ValueError): report_entries({"install":[row]})
        row=self.entry(); row["download_info"]["archive_info"]["hashes"]["sha256"]="bad"
        with self.assertRaises(ValueError): report_entries({"install":[row]})

    def test_rejects_duplicate_or_empty(self):
        row=self.entry()
        with self.assertRaises(ValueError): report_entries({"install":[row,copy.deepcopy(row)]})
        with self.assertRaises(ValueError): report_entries({"install":[]})

if __name__=="__main__": unittest.main()
