import zipfile
import xml.etree.ElementTree as ET
import sys
import os

sys.stdout.reconfigure(encoding='utf-8')

def read_docx(path):
    if not os.path.exists(path):
        print(f"File not found: {path}")
        return

    try:
        with zipfile.ZipFile(path) as docx:
            xml_content = docx.read('word/document.xml')
            tree = ET.fromstring(xml_content)

            # Namespace for Word XML
            ns = {'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
            
            paragraphs = []
            for para in tree.findall('.//w:p', ns):
                texts = [node.text for node in para.findall('.//w:t', ns) if node.text]
                if texts:
                    paragraphs.append("".join(texts))
            
            print("\n".join(paragraphs))
    except Exception as e:
        print(f"Error reading docx: {e}")

if __name__ == '__main__':
    if len(sys.argv) > 1:
        read_docx(sys.argv[1])
    else:
        print("Provide docx path")
