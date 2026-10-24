using System;

var arr = new int[0];
System.Collections.IList list = arr;
try {
    list.Add(1);
    Console.WriteLine("No exception");
} catch (NotSupportedException) {
    Console.WriteLine("NotSupportedException thrown");
}
